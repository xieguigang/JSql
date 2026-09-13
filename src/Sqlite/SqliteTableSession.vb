Imports System.IO
Imports Microsoft.VisualBasic.Data.IO.ManagedSqlite.Writer
Imports JSql.Storage

''' <summary>
''' 以 SQLite 数据库文件为物理载体的表级会话。
''' <para>
''' 桥接托管 SQLite3 写入引擎（<see cref="Sqlite3Writer"/> /
''' <see cref="Sqlite3TableWriter"/>）：读路径直接从共享的内存表模型枚举；
''' 写路径把 JSql 的整表行集翻译为底层行级原语（纯追加走 <c>AddRow</c> 快路径，
''' 其余情况整表重建），实际落盘由 <see cref="Merge"/> / <see cref="Flush"/> 触发
''' <see cref="Sqlite3Writer.Commit"/>（整库文件重建）。
''' </para>
''' </summary>
Public Class SqliteTableSession
    Implements ITableSession

    Private ReadOnly _storage As SqliteStorage
    Private ReadOnly _db As String
    Private ReadOnly _writer As Sqlite3Writer
    Private ReadOnly _options As StorageOptions
    Private ReadOnly _schemaPath As String

    Private _schema As TableSchema
    Private _lastSchemaJson As String
    Private _table As Sqlite3TableWriter

    ''' <summary>上次已知的存储状态所对应的规范化值数组（按 schema 列序）</summary>
    Private _baseline As New List(Of Object())
    Private _pending As Long

    Public Event Info(message As String) Implements ITableSession.Info

    Friend Sub New(storage As SqliteStorage, db As String, table As String,
                   schema As TableSchema, schemaPath As String, dataPath As String,
                   options As StorageOptions)

        _storage = storage
        _db = db
        _options = If(options, New StorageOptions())
        _schema = schema
        Me.TableName = table
        Me.SchemaFilePath = schemaPath
        Me.DataFilePath = dataPath
        _schemaPath = schemaPath

        _lastSchemaJson = If(File.Exists(schemaPath), SchemaStore.Serialize(schema), Nothing)
        _writer = storage.GetWriter(db)
        _table = storage.TryGetTableWriter(db, table)
    End Sub

    Public ReadOnly Property Layout As String Implements ITableSession.Layout
        Get
            Return "SQLITE"
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

    ''' <summary>SQLite 后端没有独立的 WAL 文件</summary>
    Public ReadOnly Property LogFilePath As String Implements ITableSession.LogFilePath
        Get
            Return ""
        End Get
    End Property

    Public ReadOnly Property LineCount As Long Implements ITableSession.LineCount
        Get
            Return If(_table Is Nothing, 0L, CLng(_table.RowCount))
        End Get
    End Property

    Public ReadOnly Property PendingOperations As Long Implements ITableSession.PendingOperations
        Get
            Return _pending
        End Get
    End Property

    Public ReadOnly Property PendingBufferedLines As Long Implements ITableSession.PendingBufferedLines
        Get
            Return 0
        End Get
    End Property

    Public ReadOnly Property HasPendingChanges As Boolean Implements ITableSession.HasPendingChanges
        Get
            Return _writer.IsDirty
        End Get
    End Property

    Public ReadOnly Property WalFileSize As Long Implements ITableSession.WalFileSize
        Get
            Return 0
        End Get
    End Property

    Public ReadOnly Property DataFileSize As Long Implements ITableSession.DataFileSize
        Get
            Return FileSize(Me.DataFilePath)
        End Get
    End Property

    Private Shared Function FileSize(path As String) As Long
        If path IsNot Nothing AndAlso File.Exists(path) Then
            Return New FileInfo(path).Length
        End If

        Return 0
    End Function

    ''' <summary>从共享的内存表模型枚举全部行，并记录基线用于后续差异同步。</summary>
    Public Function ReadRows() As List(Of Dictionary(Of String, Object)) Implements ITableSession.ReadRows
        Dim rows As New List(Of Dictionary(Of String, Object))
        Dim baseline As New List(Of Object())

        If _table IsNot Nothing Then
            For Each record As Sqlite3DataRow In _table.EnumerateRows()
                Dim row As Dictionary(Of String, Object) = SqliteSchemaMapper.FromValues(record.Values, _schema)

                rows.Add(row)
                baseline.Add(SqliteSchemaMapper.ToValues(row, _schema))
            Next
        End If

        _baseline = baseline
        Return rows
    End Function

    ''' <summary>
    ''' 把整表行集与基线做差异比较：公共首尾相同的行被裁剪，纯追加走一条条
    ''' <c>AddRow</c>；存在修改/删除时整表重建（托管引擎没有行级 splice 原语，
    ''' 且提交时本就会整文件重建）。
    ''' </summary>
    Public Sub SyncRows(rows As List(Of Dictionary(Of String, Object))) Implements ITableSession.SyncRows
        If _schema.Columns.Count = 0 Then
            Return
        End If

        If _table Is Nothing Then
            _table = _storage.GetOrCreateTableWriter(_db, _schema.TableName, _schema)
        End If

        Dim newValues As New List(Of Object())

        For Each row In rows
            newValues.Add(SqliteSchemaMapper.ToValues(row, _schema))
        Next

        Dim changed As Integer = ApplyDiff(newValues)

        _baseline = newValues

        If changed > 0 Then
            _pending += changed
        End If
    End Sub

    Private Function ApplyDiff(newValues As List(Of Object())) As Integer
        Dim oldCount As Integer = _baseline.Count
        Dim newCount As Integer = newValues.Count
        Dim head As Integer = 0

        While head < oldCount AndAlso head < newCount AndAlso ValuesEqual(_baseline(head), newValues(head))
            head += 1
        End While

        Dim tail As Integer = 0

        While tail < oldCount - head AndAlso tail < newCount - head AndAlso
              ValuesEqual(_baseline(oldCount - 1 - tail), newValues(newCount - 1 - tail))
            tail += 1
        End While

        Dim deleted As Integer = oldCount - head - tail
        Dim appended As Integer = newCount - head - tail

        If deleted = 0 Then
            ' 纯追加（含完全相同的情况：此时 appended = 0）
            For i As Integer = head To newCount - 1
                Call _table.AddRow(newValues(i))
            Next

            Return appended
        End If

        ' 存在删除或行内修改：整表重建
        _table.Clear()

        For Each rowValues In newValues
            Call _table.AddRow(rowValues)
        Next

        Return Math.Max(deleted, appended)
    End Function

    Private Shared Function ValuesEqual(a As Object(), b As Object()) As Boolean
        If a Is Nothing OrElse b Is Nothing Then
            Return a Is b
        End If

        If a.Length <> b.Length Then
            Return False
        End If

        For i As Integer = 0 To a.Length - 1
            Dim x As Object = a(i)
            Dim y As Object = b(i)

            If x Is Nothing AndAlso y Is Nothing Then
                Continue For
            End If

            If x Is Nothing OrElse y Is Nothing Then
                Return False
            End If

            If Not x.Equals(y) Then
                Return False
            End If
        Next

        Return True
    End Function

    ''' <summary>
    ''' 写 schema 文件（内容变化时），并保证 SQLite 侧的列集合与 schema 一致
    ''' （列集合变化时先删表再建表）。
    ''' </summary>
    Public Sub SaveSchema(schema As TableSchema) Implements ITableSession.SaveSchema
        Dim json As String = SchemaStore.Serialize(schema)

        If Not String.Equals(json, _lastSchemaJson, StringComparison.Ordinal) Then
            SchemaStore.Write(_schemaPath, schema)
            _lastSchemaJson = json

            _storage.EnsureTableSchema(_db, schema.TableName, schema)
            _table = _storage.TryGetTableWriter(_db, schema.TableName)
        End If

        _schema = schema
    End Sub

    ''' <summary>把整个数据库的内存模型提交到磁盘（幂等）。</summary>
    Public Sub Flush() Implements ITableSession.Flush
        Commit()
    End Sub

    ''' <summary>把整个数据库的内存模型提交到磁盘（幂等）。</summary>
    Public Sub Merge() Implements ITableSession.Merge
        Commit()
    End Sub

    ''' <summary>
    ''' 把整个数据库的内存模型提交到磁盘。SQLite 后端没有独立的 WAL 文件，也不存在
    ''' 「读枚举未完成」的限制，因此提交总是成功（幂等）。
    ''' </summary>
    Public Function TryMerge() As Boolean Implements ITableSession.TryMerge
        Commit()
        Return True
    End Function

    Private Sub Commit()
        If _writer.IsDirty Then
            _writer.Commit()
            _pending = 0

            If _options.Verbose Then
                RaiseEvent Info("sqlite commit: database '" & _db & "' written")
            End If
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' 共享的 Sqlite3Writer 由 SqliteStorage 统一负责提交与释放，这里不释放它
    End Sub
End Class
