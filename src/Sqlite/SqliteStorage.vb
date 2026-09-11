Imports System.IO
Imports Microsoft.VisualBasic.Data.IO.ManagedSqlite.Core.SQLSchema
Imports Microsoft.VisualBasic.Data.IO.ManagedSqlite.Writer
Imports JSql.Storage

''' <summary>
''' 基于 SQLite 数据库文件的存储后端：<b>一个 JSql 数据库 = 一个 .sqlite 文件</b>，
''' 库内所有表共存于同一个 SQLite 文件。<para>
''' </para>
''' 行数据写入 SQLite 文件的表；JSql 的完整模式元数据（列类型原始文本、COMMENT、
''' DEFAULT、表级 UNIQUE KEY/KEY 等 SQLite DDL 无法承载的信息）与列索引
''' （<c>.indexes</c>）保存在库的辅助目录 <c>&lt;root&gt;\.jsql\&lt;db&gt;\</c> 之中，
''' 从而复用 <see cref="SchemaStore"/> 与 <c>IndexPersistence</c>，并让
''' <c>DESCRIBE</c> / <c>SHOW STORAGE</c> / 列索引全部照常工作。
''' </summary>
Public Class SqliteStorage
    Implements IDbFileStorageProvider

    ''' <summary>SQLite 数据库文件扩展名</summary>
    Public Const SqliteDataExtension As String = ".sqlite"

    ''' <summary>辅助目录名（存放 schema 文件与列索引）</summary>
    Public Const AuxiliaryDirectory As String = ".jsql"

    Private ReadOnly _rootPath As String
    Private ReadOnly _options As StorageOptions
    Private ReadOnly _sessions As TableSessionPool
    Private ReadOnly _writers As New Dictionary(Of String, Sqlite3Writer)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _gate As New Object()

    Sub New(root As String, Optional options As StorageOptions = Nothing)
        _rootPath = Path.GetFullPath(root)
        Me.Root = _rootPath
        _options = If(options, New StorageOptions())
        _sessions = New TableSessionPool(_options)

        If Not Directory.Exists(_rootPath) Then
            Directory.CreateDirectory(_rootPath)
        End If
    End Sub

    Public ReadOnly Property ProviderName As String Implements IDbFileStorageProvider.ProviderName
        Get
            Return "sqlite"
        End Get
    End Property

    Public ReadOnly Property Root As String Implements IDbFileStorageProvider.Root
    Public Property CurrentDatabase As String Implements IDbFileStorageProvider.CurrentDatabase
    Public ReadOnly Property Options As StorageOptions Implements IDbFileStorageProvider.Options
        Get
            Return _options
        End Get
    End Property

    Public ReadOnly Property Sessions As TableSessionPool Implements IDbFileStorageProvider.Sessions
        Get
            Return _sessions
        End Get
    End Property

    ' ==================== paths ====================

    ''' <summary>数据库文件路径：&lt;root&gt;\&lt;db&gt;.sqlite</summary>
    Public Function DbFilePath(db As String) As String
        TextFileStorage.ValidateName(db, "database")
        Return Path.Combine(_rootPath, db & SqliteDataExtension)
    End Function

    ''' <summary>辅助目录：存放 &lt;表&gt;.schema.json 与 .indexes</summary>
    Public Function DatabaseDir(db As String) As String Implements IDbFileStorageProvider.DatabaseDir
        TextFileStorage.ValidateName(db, "database")
        Return Path.Combine(_rootPath, AuxiliaryDirectory, db)
    End Function

    Private Function SchemaPath(db As String, table As String) As String
        Return Path.Combine(DatabaseDir(db), table & ".schema.json")
    End Function

    ' ==================== 库级操作 ====================

    Public Function DatabaseExists(name As String) As Boolean Implements IDbFileStorageProvider.DatabaseExists
        Return File.Exists(DbFilePath(name))
    End Function

    Public Function GetDatabases() As List(Of String) Implements IDbFileStorageProvider.GetDatabases
        Dim names As New List(Of String)

        If Not Directory.Exists(_rootPath) Then
            Return names
        End If

        For Each file As String In Directory.GetFiles(_rootPath, "*" & SqliteDataExtension)
            names.Add(Path.GetFileNameWithoutExtension(file))
        Next

        names.Sort(StringComparer.OrdinalIgnoreCase)
        Return names
    End Function

    Public Sub CreateDatabase(name As String) Implements IDbFileStorageProvider.CreateDatabase
        Dim path As String = DbFilePath(name)

        If Not File.Exists(path) Then
            Using writer As Sqlite3Writer = Sqlite3Writer.CreateFile(path)
                ' CreateFile 已经写入合法的空库
            End Using
        End If

        Dim aux As String = DatabaseDir(name)

        If Not Directory.Exists(aux) Then
            Directory.CreateDirectory(aux)
        End If
    End Sub

    Public Sub DropDatabase(name As String) Implements IDbFileStorageProvider.DropDatabase
        Dim aux As String = DatabaseDir(name)

        _sessions.CloseDatabase(aux)
        DisposeWriter(name)

        DeleteFileIfExists(DbFilePath(name))
        DeleteFileIfExists(DbFilePath(name) & ".tmp")

        If Directory.Exists(aux) Then
            Try
                Directory.Delete(aux, recursive:=True)
            Catch ex As IOException
                Console.Error.WriteLine("[sqlite] can not delete " & aux & ": " & ex.Message)
            End Try
        End If
    End Sub

    ' ==================== 表级目录 ====================

    Public Function TableExists(db As String, table As String) As Boolean Implements IDbFileStorageProvider.TableExists
        If Not DatabaseExists(db) Then
            Return False
        End If

        Return HasTable(GetWriter(db), table)
    End Function

    Public Function GetTables(db As String) As List(Of String) Implements IDbFileStorageProvider.GetTables
        Dim names As New List(Of String)

        If Not DatabaseExists(db) Then
            Return names
        End If

        names.AddRange(GetWriter(db).GetTableNames())
        names.Sort(StringComparer.OrdinalIgnoreCase)
        Return names
    End Function

    ''' <summary>SQLite 后端没有旧版需要迁移的布局</summary>
    Public Function IsLegacyTable(db As String, table As String) As Boolean Implements IDbFileStorageProvider.IsLegacyTable
        Return False
    End Function

    Public Function FindTableFile(db As String, table As String) As String Implements IDbFileStorageProvider.FindTableFile
        If TableExists(db, table) Then
            Return DbFilePath(db)
        End If

        Return Nothing
    End Function

    ' ==================== writer 生命周期 ====================

    ''' <summary>获取（按需打开或创建）某个数据库的写入器；同一数据库共享一个实例。</summary>
    Friend Function GetWriter(db As String) As Sqlite3Writer
        Dim path As String = DbFilePath(db)

        SyncLock _gate
            Dim writer As Sqlite3Writer = Nothing

            If _writers.TryGetValue(path, writer) Then
                Return writer
            End If

            If File.Exists(path) Then
                writer = Sqlite3Writer.OpenFile(path)
            Else
                writer = Sqlite3Writer.CreateFile(path)
            End If

            _writers(path) = writer
            Return writer
        End SyncLock
    End Function

    Private Sub DisposeWriter(db As String)
        Dim path As String = DbFilePath(db)
        Dim writer As Sqlite3Writer = Nothing

        SyncLock _gate
            If _writers.TryGetValue(path, writer) Then
                _writers.Remove(path)
            End If
        End SyncLock

        If writer IsNot Nothing Then
            Try
                ' AutoCommitOnDispose：把尚未提交的内存模型落盘
                writer.Dispose()
            Catch ex As Exception
                Console.Error.WriteLine("[sqlite] commit on close failed for " & path & ": " & ex.Message)
            End Try
        End If
    End Sub

    Friend Function HasTable(writer As Sqlite3Writer, table As String) As Boolean
        Return writer.GetTableNames().Any(Function(n) String.Equals(n, table, StringComparison.OrdinalIgnoreCase))
    End Function

    ''' <summary>已存在表对应的可写模型，不存在时返回 Nothing。</summary>
    Friend Function TryGetTableWriter(db As String, table As String) As Sqlite3TableWriter
        Dim writer As Sqlite3Writer = GetWriter(db)

        If HasTable(writer, table) Then
            Return writer.GetTable(table)
        End If

        Return Nothing
    End Function

    ''' <summary>确保存在与 schema 列集合匹配的 SQLite 表（列集合变化时重建）。</summary>
    Friend Sub EnsureTableSchema(db As String, table As String, schema As TableSchema)
        If schema Is Nothing OrElse schema.Columns.Count = 0 Then
            Return
        End If

        Dim writer As Sqlite3Writer = GetWriter(db)

        If HasTable(writer, table) Then
            Dim existing As Sqlite3TableWriter = writer.GetTable(table)

            If ColumnsMatch(existing.Columns, schema) Then
                Return
            End If

            Call writer.DropTable(table)
        End If

        Call writer.CreateTable(table, SqliteSchemaMapper.ToSqliteColumns(schema))
    End Sub

    ''' <summary>按需创建（或复用）指定表的可写模型。</summary>
    Friend Function GetOrCreateTableWriter(db As String, table As String, schema As TableSchema) As Sqlite3TableWriter
        Dim writer As Sqlite3Writer = GetWriter(db)

        If HasTable(writer, table) Then
            Return writer.GetTable(table)
        End If

        Return writer.CreateTable(table, SqliteSchemaMapper.ToSqliteColumns(schema))
    End Function

    Private Shared Function ColumnsMatch(actual As Sqlite3Column(), schema As TableSchema) As Boolean
        If actual Is Nothing OrElse actual.Length <> schema.Columns.Count Then
            Return False
        End If

        For i As Integer = 0 To actual.Length - 1
            Dim col As ColumnDef = schema.Columns(i)

            If Not String.Equals(actual(i).Name, col.Name, StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If

            If Not String.Equals(If(actual(i).Type, ""),
                                 SqliteSchemaMapper.ToSqliteType(col.TypeName),
                                 StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If
        Next

        Return True
    End Function

    ' ==================== 会话访问 ====================

    Public Function OpenSession(db As String, table As String) As ITableSession Implements IDbFileStorageProvider.OpenSession
        Dim aux As String = DatabaseDir(db)
        Dim schemaFile As String = SchemaPath(db, table)
        Dim schema As TableSchema

        If File.Exists(schemaFile) Then
            schema = SchemaStore.Read(schemaFile)
        Else
            ' 外部 sqlite 文件或尚未保存 schema 的新表：尝试从 SQLite 侧反解
            Dim existing As Sqlite3TableWriter = TryGetTableWriter(db, table)

            schema = If(existing IsNot Nothing,
                        SchemaOf(existing, table),
                        New TableSchema With {.TableName = table})
        End If

        Dim dataPath As String = DbFilePath(db)

        Return _sessions.GetOrOpen(aux, table,
            Function() New SqliteTableSession(Me, db, table, schema, schemaFile, dataPath, _options))
    End Function

    Public Function TryGetSession(db As String, table As String) As ITableSession Implements IDbFileStorageProvider.TryGetSession
        Return _sessions.TryGet(DatabaseDir(db), table)
    End Function

    ''' <summary>由 SQLite 表的 DDL 反解出 JSql table schema（回退到列定义）。</summary>
    Private Shared Function SchemaOf(table As Sqlite3TableWriter, name As String) As TableSchema
        Dim sql As String = table.Sql

        If Not String.IsNullOrWhiteSpace(sql) Then
            Try
                Return SqliteSchemaMapper.FromSqliteSchema(New Schema(sql, removeNameEscape:=True), name)
            Catch ex As Exception
                ' 反解失败时回退到写入器已经解析好的列定义
            End Try
        End If

        Dim schema As New TableSchema With {.TableName = name}

        For Each col As Sqlite3Column In table.Columns
            schema.Columns.Add(New ColumnDef With {
                .Name = col.Name,
                .TypeName = SqliteSchemaMapper.ToCanonicalType(col.Type),
                .RawType = col.Type,
                .NotNull = col.NotNull,
                .PrimaryKey = col.PrimaryKey
            })
        Next

        Return schema
    End Function

    ' ==================== 加载 / 保存 ====================

    Public Function LoadTable(db As String, table As String) As StoredTable Implements IDbFileStorageProvider.LoadTable
        If Not TableExists(db, table) Then
            Throw New ArgumentException("table '" & table & "' not found in database '" & db & "'!")
        End If

        Dim session As ITableSession = OpenSession(db, table)

        Return New StoredTable With {
            .Schema = session.Schema,
            .Rows = session.ReadRows()
        }
    End Function

    Public Sub SaveTable(db As String, table As StoredTable) Implements IDbFileStorageProvider.SaveTable
        Dim session As ITableSession = OpenSession(db, table.Schema.TableName)

        session.SaveSchema(table.Schema)
        session.SyncRows(table.Rows)
    End Sub

    Public Sub DeleteTable(db As String, table As String) Implements IDbFileStorageProvider.DeleteTable
        _sessions.Close(DatabaseDir(db), table)

        Dim writer As Sqlite3Writer = GetWriter(db)

        If writer.DropTable(table) Then
            writer.Commit()
        End If

        DeleteFileIfExists(SchemaPath(db, table))
    End Sub

    ' ==================== 诊断 ====================

    Public Function DescribeStorage(db As String) As List(Of Object()) Implements IDbFileStorageProvider.DescribeStorage
        Dim rows As New List(Of Object())

        For Each name As String In GetTables(db)
            Dim session As ITableSession = OpenSession(db, name)

            rows.Add(New Object() {name, session.Layout, session.LineCount, session.PendingOperations,
                                   session.WalFileSize, session.DataFileSize, Path.GetFileName(DbFilePath(db))})
        Next

        Return rows
    End Function

    Private Shared Sub DeleteFileIfExists(path As String)
        Try
            If File.Exists(path) Then
                File.Delete(path)
            End If
        Catch ex As IOException
            Console.Error.WriteLine("[sqlite] can not delete " & path & ": " & ex.Message)
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' 先把每个会话尚未提交的内存模型合并落盘，再释放共享的 writer
        _sessions.DisposeAll()

        Dim writers As List(Of Sqlite3Writer)

        SyncLock _gate
            writers = _writers.Values.ToList()
            _writers.Clear()
        End SyncLock

        For Each writer As Sqlite3Writer In writers
            Try
                writer.Dispose()
            Catch ex As Exception
                Console.Error.WriteLine("[sqlite] dispose failed: " & ex.Message)
            End Try
        Next
    End Sub
End Class
