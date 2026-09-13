Imports System.IO
Imports Microsoft.VisualBasic.Data.Repository

Namespace Storage

    ''' <summary>
    ''' 通用表会话：schema 文件 + 由 <see cref="TextLineStore"/> 管理的行式数据文件（含 WAL）。
    ''' 行的具体格式（JSONL / CSV …）由注入的 <see cref="IRowCodec"/> 决定，
    ''' 所有修改都被翻译为底层的行级 splice 原语。
    ''' </summary>
    Public Class TextTableSession : Implements ITableSession

        Private ReadOnly _options As StorageOptions
        Private ReadOnly _store As TextLineStore
        Private ReadOnly _codec As IRowCodec
        Private ReadOnly _schemaPath As String

        Private _schema As TableSchema
        ''' <summary>上次已知的存储状态所对应的物理行（包含 CSV 表头行）</summary>
        Private _baseline As New List(Of String)
        Private _lastSchemaJson As String

        Public Event Info(message As String) Implements ITableSession.Info

        Sub New(schema As TableSchema, schemaPath As String, dataPath As String,
                options As StorageOptions, codec As IRowCodec)

            If codec Is Nothing Then Throw New ArgumentNullException(NameOf(codec))

            Me.TableName = schema.TableName
            Me.SchemaFilePath = schemaPath
            _schema = schema
            _options = options
            _codec = codec
            _schemaPath = schemaPath

            ' when the schema file does not exist yet the first save must write it,
            ' so the serialized form is only remembered for an existing file
            _lastSchemaJson = If(File.Exists(schemaPath), SchemaStore.Serialize(schema), Nothing)

            _store = New TextLineStore(dataPath, options.CreateStoreOptions())
            AddHandler _store.Info, AddressOf OnStoreInfo
            _store.Open()

            ' 读取数据文件的物理表头并建立列序映射（JSONL 无表头，会自动忽略）
            If _codec.HasHeader Then
                Dim headerLine As String = Nothing

                If _store.TotalLines > 0 Then
                    headerLine = _store.ReadLine(1)
                End If

                _codec.UseHeader(headerLine, _schema)
            End If
        End Sub

        Private Sub OnStoreInfo(message As String)
            RaiseEvent Info(message)
        End Sub

        Public ReadOnly Property Layout As String Implements ITableSession.Layout
            Get
                Return _codec.LayoutName
            End Get
        End Property

        Public ReadOnly Property Schema As TableSchema Implements ITableSession.Schema
            Get
                Return _schema
            End Get
        End Property

        Public ReadOnly Property TableName As String Implements ITableSession.TableName
        Public ReadOnly Property SchemaFilePath As String Implements ITableSession.SchemaFilePath

        Public ReadOnly Property DataFilePath As String Implements ITableSession.DataFilePath
            Get
                Return _store.DataFilePath
            End Get
        End Property

        Public ReadOnly Property LogFilePath As String Implements ITableSession.LogFilePath
            Get
                Return _store.LogFilePath
            End Get
        End Property

        Public ReadOnly Property LineCount As Long Implements ITableSession.LineCount
            Get
                Dim total As Long = _store.TotalLines

                ' 有表头行的格式（CSV）里物理首行是表头，不计入数据行数
                If _codec.HasHeader AndAlso total > 0 Then
                    Return total - 1
                End If

                Return total
            End Get
        End Property

        Public ReadOnly Property PendingOperations As Long Implements ITableSession.PendingOperations
            Get
                Return _store.PendingOperationCount
            End Get
        End Property

        Public ReadOnly Property PendingBufferedLines As Long Implements ITableSession.PendingBufferedLines
            Get
                Return _store.PendingBufferedLineCount
            End Get
        End Property

        Public ReadOnly Property HasPendingChanges As Boolean Implements ITableSession.HasPendingChanges
            Get
                Return _store.HasPendingChanges
            End Get
        End Property

        Public ReadOnly Property WalFileSize As Long Implements ITableSession.WalFileSize
            Get
                Return FileSize(_store.LogFilePath)
            End Get
        End Property

        Public ReadOnly Property DataFileSize As Long Implements ITableSession.DataFileSize
            Get
                Return FileSize(_store.DataFilePath)
            End Get
        End Property

        Private Shared Function FileSize(path As String) As Long
            If path IsNot Nothing AndAlso File.Exists(path) Then
                Return New FileInfo(path).Length
            End If

            Return 0
        End Function

        ''' <summary>
        ''' read every row. the enumeration is materialized on purpose: the store
        ''' refuses to merge while a read enumeration is still active. the physical
        ''' header line (if any) is kept in the baseline but is not returned as a row.
        ''' </summary>
        Public Function ReadRows() As List(Of Dictionary(Of String, Object)) Implements ITableSession.ReadRows
            Dim lines As New List(Of String)
            Dim rows As New List(Of Dictionary(Of String, Object))

            For Each line As String In _store.ReadLines()
                lines.Add(line)

                If Not IsHeaderLine(lines.Count) Then
                    rows.Add(_codec.DeserializeLine(line, _schema))
                End If
            Next

            _baseline = lines
            Return rows
        End Function

        ''' <summary>
        ''' translate the row difference into a single line level splice. the common
        ''' head and tail are trimmed first, so a bulk insert becomes a pure append,
        ''' an update becomes an in place line replacement and a delete becomes a
        ''' range removal. when the format has a header line it is always the first
        ''' physical line, so the splice offset is naturally correct.
        ''' </summary>
        Public Sub SyncRows(rows As List(Of Dictionary(Of String, Object))) Implements ITableSession.SyncRows
            Dim lines As New List(Of String)

            If _codec.HasHeader Then
                lines.Add(_codec.BuildHeader(_schema))
            End If

            For Each row In rows
                lines.Add(_codec.SerializeLine(row, _schema))
            Next

            Dim oldCount As Integer = _baseline.Count
            Dim newCount As Integer = lines.Count
            Dim head As Integer = 0

            While head < oldCount AndAlso head < newCount AndAlso
                  String.Equals(_baseline(head), lines(head), StringComparison.Ordinal)
                head += 1
            End While

            Dim tail As Integer = 0

            While tail < oldCount - head AndAlso tail < newCount - head AndAlso
                  String.Equals(_baseline(oldCount - 1 - tail), lines(newCount - 1 - tail), StringComparison.Ordinal)
                tail += 1
            End While

            Dim deleted As Integer = oldCount - head - tail
            Dim window As New List(Of String)()

            For i As Integer = head To newCount - tail - 1
                window.Add(lines(i))
            Next

            If deleted = 0 AndAlso head >= oldCount Then
                ' pure append, the store fast path: one log record, one fsync
                _store.AppendLines(window)
            ElseIf deleted = 0 Then
                If window.Count > 0 Then
                    _store.InsertLines(head + 1, window)
                End If
            Else
                _store.ReplaceLines(head + 1, deleted, window)
            End If

            _baseline = lines

            If Not _options.FsyncEachWrite Then
                ' one flush per sql statement instead of one per write primitive
                _store.FlushLog()
            End If
        End Sub

        Public Sub SaveSchema(schema As TableSchema) Implements ITableSession.SaveSchema
            Dim json As String = SchemaStore.Serialize(schema)

            If Not String.Equals(json, _lastSchemaJson, StringComparison.Ordinal) Then
                SchemaStore.Write(_schemaPath, schema)
                _lastSchemaJson = json
            End If

            _schema = schema
        End Sub

        Public Sub Flush() Implements ITableSession.Flush
            _store.FlushLog()
        End Sub

        Public Sub Merge() Implements ITableSession.Merge
            _store.Merge()
        End Sub

        Public Function TryMerge() As Boolean Implements ITableSession.TryMerge
            Return _store.TryMerge()
        End Function

        ''' <summary>物理第 index 行（1 基）是否为表头行。</summary>
        Private Function IsHeaderLine(index As Integer) As Boolean
            Return _codec.HasHeader AndAlso index = 1
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            RemoveHandler _store.Info, AddressOf OnStoreInfo

            Try
                _store.Dispose()
            Catch
                ' the shutdown path must never throw
            End Try
        End Sub
    End Class
End Namespace
