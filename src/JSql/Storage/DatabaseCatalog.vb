Imports System.IO

Namespace Storage

    ''' <summary>
    ''' manages the database root folder: one sub-folder is one database. a table
    ''' is stored as a schema file plus a jsonl data file, the legacy single file
    ''' json layout is still readable and gets migrated on the first write.
    ''' </summary>
    Public Class DatabaseCatalog

        Public Shared ReadOnly InvalidNameChars As Char() = {"/"c, "\"c, ":"c, "*"c, "?"c, """"c, "<"c, ">"c, "|"c}

        Private Enum TableLayout
            None
            ''' <summary>&lt;table&gt;.schema.json + &lt;table&gt;.jsonl with write ahead log</summary>
            Jsonl
            ''' <summary>&lt;table&gt;.schema.json + &lt;table&gt;.csv (header line + data rows) with write ahead log</summary>
            Csv
            ''' <summary>the legacy &lt;table&gt;.json single file table</summary>
            Legacy
        End Enum

        Public ReadOnly Property Root As String
        Public Property CurrentDatabase As String
        Public ReadOnly Property Options As StorageOptions
        Public ReadOnly Property Sessions As TableSessionPool

        Sub New(root As String, Optional options As StorageOptions = Nothing)
            Me.Root = Path.GetFullPath(root)
            Me.Options = If(options, New StorageOptions())
            Me.Sessions = New TableSessionPool(Me.Options)

            If Not Directory.Exists(Me.Root) Then
                Directory.CreateDirectory(Me.Root)
            End If
        End Sub

        Public Shared Sub ValidateName(name As String, what As String)
            If String.IsNullOrWhiteSpace(name) Then
                Throw New ArgumentException("the " & what & " name can not be empty!")
            End If

            If name.IndexOfAny(InvalidNameChars) >= 0 Then
                Throw New ArgumentException("invalid " & what & " name: '" & name & "'")
            End If
        End Sub

        Public Function DatabaseExists(name As String) As Boolean
            Return Directory.Exists(DatabaseDir(name))
        End Function

        Public Function GetDatabases() As List(Of String)
            Dim names As New List(Of String)

            For Each dir As String In Directory.GetDirectories(Root)
                Dim name As String = Path.GetFileName(dir)

                If Not name.StartsWith(".") Then
                    names.Add(name)
                End If
            Next

            names.Sort(StringComparer.OrdinalIgnoreCase)
            Return names
        End Function

        Public Sub CreateDatabase(name As String)
            ValidateName(name, "database")
            Directory.CreateDirectory(DatabaseDir(name))
        End Sub

        Public Sub DropDatabase(name As String)
            Dim dir As String = DatabaseDir(name)
            Sessions.CloseDatabase(dir)

            If Directory.Exists(dir) Then
                Directory.Delete(dir, recursive:=True)
            End If
        End Sub

        Public Function DatabaseDir(db As String) As String
            ValidateName(db, "database")
            Return Path.Combine(Root, db)
        End Function

        ' ==================== discovery ====================

        Private Function ResolveLayout(db As String, table As String) As TableLayout
            Dim dir As String = DatabaseDir(db)

            If Not Directory.Exists(dir) Then
                Return TableLayout.None
            End If

            If File.Exists(StorageLayout.CsvDataPath(dir, table)) Then
                Return TableLayout.Csv
            End If

            If File.Exists(StorageLayout.DataPath(dir, table)) Then
                Return TableLayout.Jsonl
            End If

            If File.Exists(StorageLayout.SchemaPath(dir, table)) Then
                ' 只有 schema、尚无数据文件：按配置的默认格式判定
                Return If(Options.Format = StorageFormat.Csv, TableLayout.Csv, TableLayout.Jsonl)
            End If

            If File.Exists(StorageLayout.LegacyPath(dir, table)) Then
                Return TableLayout.Legacy
            End If

            Return TableLayout.None
        End Function

        ''' <summary>
        ''' the primary data file of a table: the jsonl data file or the legacy json
        ''' file. nothing when the table does not exist.
        ''' </summary>
        Public Function FindTableFile(db As String, table As String) As String
            Select Case ResolveLayout(db, table)
                Case TableLayout.Csv
                    Return StorageLayout.CsvDataPath(DatabaseDir(db), table)
                Case TableLayout.Jsonl
                    Return StorageLayout.DataPath(DatabaseDir(db), table)
                Case TableLayout.Legacy
                    Return StorageLayout.LegacyPath(DatabaseDir(db), table)
                Case Else
                    Return Nothing
            End Select
        End Function

        Public Function TableExists(db As String, table As String) As Boolean
            Return ResolveLayout(db, table) <> TableLayout.None
        End Function

        ''' <summary>
        ''' the table names of a database: one name per table, auxiliary files of
        ''' the jsonl store are filtered out.
        ''' </summary>
        Public Function GetTables(db As String) As List(Of String)
            Return StorageLayout.ListTables(DatabaseDir(db))
        End Function

        ''' <summary>true when the table uses the legacy single file json layout</summary>
        Public Function IsLegacyTable(db As String, table As String) As Boolean
            Return ResolveLayout(db, table) = TableLayout.Legacy
        End Function

        ' ==================== session access ====================

        ''' <summary>
        ''' open (or reuse) the session of one table. the row format is decided by the
        ''' extension of the existing data file; for a brand new table (or when a legacy
        ''' single file table is migrated) the configured <see cref="StorageOptions.Format"/>
        ''' is used.
        ''' </summary>
        Public Function OpenSession(db As String, table As String) As ITableSession
            Dim dir As String = DatabaseDir(db)
            Dim schemaPath As String = StorageLayout.SchemaPath(dir, table)
            Dim schema As TableSchema

            If File.Exists(schemaPath) Then
                schema = SchemaStore.Read(schemaPath)
            Else
                schema = New TableSchema With {.TableName = table}
            End If

            Dim useCsv As Boolean

            Select Case ResolveLayout(db, table)
                Case TableLayout.Csv
                    useCsv = True
                Case TableLayout.Jsonl
                    useCsv = False
                Case Else
                    useCsv = Options.Format = StorageFormat.Csv
            End Select

            Dim format As StorageFormat = If(useCsv, StorageFormat.Csv, StorageFormat.Jsonl)
            Dim dataPath As String = StorageLayout.DataPathForFormat(dir, table, format)

            Return Sessions.GetOrOpen(dir, table, Function() NewSession(useCsv, schema, schemaPath, dataPath))
        End Function

        Private Function NewSession(useCsv As Boolean, schema As TableSchema, schemaPath As String, dataPath As String) As ITableSession
            If useCsv Then
                Return New CsvTableSession(schema, schemaPath, dataPath, Options)
            End If

            Return New JsonlTableSession(schema, schemaPath, dataPath, Options)
        End Function

        ''' <summary>session of an already opened table, nothing when it is not open</summary>
        Public Function TryGetSession(db As String, table As String) As ITableSession
            Return Sessions.TryGet(DatabaseDir(db), table)
        End Function

        ' ==================== load / save ====================

        ''' <summary>load the schema and the rows of a table</summary>
        Public Function LoadTable(db As String, table As String) As StoredTable
            Select Case ResolveLayout(db, table)
                Case TableLayout.None
                    Throw New ArgumentException("table '" & table & "' not found in database '" & db & "'!")

                Case TableLayout.Legacy
                    Return SchemaStore.ReadLegacy(StorageLayout.LegacyPath(DatabaseDir(db), table))

                Case Else
                    Dim session As ITableSession = OpenSession(db, table)

                    Return New StoredTable With {
                        .Schema = session.Schema,
                        .Rows = session.ReadRows()
                    }
            End Select
        End Function

        ''' <summary>
        ''' persist a table. the schema file is written when it changed, the rows are
        ''' synchronized line by line through the write ahead log of the jsonl store.
        ''' </summary>
        Public Sub SaveTable(db As String, table As StoredTable)
            Dim dir As String = DatabaseDir(db)
            Dim name As String = table.Schema.TableName
            Dim legacyPath As String = StorageLayout.LegacyPath(dir, name)
            Dim layout As TableLayout = ResolveLayout(db, name)
            Dim hasEngine As Boolean = layout = TableLayout.Jsonl OrElse layout = TableLayout.Csv

            If Options.LegacyJson AndAlso Not hasEngine Then
                ' explicit legacy mode: keep the old whole file json layout
                Call New JsonTableStore().Write(legacyPath, table)
                Return
            End If

            Dim session As ITableSession = OpenSession(db, name)

            session.SaveSchema(table.Schema)
            session.SyncRows(table.Rows)

            If Not hasEngine AndAlso File.Exists(legacyPath) Then
                ' the legacy file has been imported into the jsonl layout, keep it as
                ' a backup instead of silently deleting the original data
                Dim backup As String = StorageLayout.LegacyBackupPath(dir, name)

                If File.Exists(backup) Then
                    File.Delete(backup)
                End If

                File.Move(legacyPath, backup)
            End If
        End Sub

        ''' <summary>
        ''' drop a table: the session is closed first so that the exclusive lock is
        ''' released, then every data file and auxiliary file is removed.
        ''' </summary>
        Public Sub DeleteTable(db As String, table As String)
            Dim dir As String = DatabaseDir(db)

            Sessions.Close(dir, table)

            DeleteFileIfExists(StorageLayout.SchemaPath(dir, table))
            DeleteFileIfExists(StorageLayout.DataPath(dir, table))
            DeleteFileIfExists(StorageLayout.CsvDataPath(dir, table))
            DeleteFileIfExists(StorageLayout.LegacyPath(dir, table))
            DeleteFileIfExists(StorageLayout.LegacyBackupPath(dir, table))

            For Each file As String In StorageLayout.CompanionFiles(dir, table)
                DeleteFileIfExists(file)
            Next
        End Sub

        Private Shared Sub DeleteFileIfExists(path As String)
            Try
                If File.Exists(path) Then
                    File.Delete(path)
                End If
            Catch ex As IOException
                ' a still locked companion file must not fail the whole DROP statement
                Console.Error.WriteLine("[store] can not delete " & path & ": " & ex.Message)
            End Try
        End Sub

        ' ==================== diagnostics ====================

        ''' <summary>
        ''' storage status of every table of a database, used by SHOW STORAGE.
        ''' columns: Table, Layout, Rows, Pending, WalBytes, DataBytes, File
        ''' </summary>
        Public Function DescribeStorage(db As String) As List(Of Object())
            Dim rows As New List(Of Object())

            For Each name As String In GetTables(db)
                Dim dir As String = DatabaseDir(db)

                If ResolveLayout(db, name) = TableLayout.Legacy Then
                    Dim filePath As String = StorageLayout.LegacyPath(dir, name)
                    Dim size As Long = If(System.IO.File.Exists(filePath), New FileInfo(filePath).Length, 0L)

                    rows.Add(New Object() {name, "JSON", -1L, 0L, 0L, size, Path.GetFileName(filePath)})
                Else
                    Dim session As ITableSession = OpenSession(db, name)

                    rows.Add(New Object() {name, session.Layout, session.LineCount, session.PendingOperations,
                                           session.WalFileSize, session.DataFileSize, Path.GetFileName(session.DataFilePath)})
                End If
            Next

            Return rows
        End Function

        Public Sub Dispose()
            Sessions.DisposeAll()
        End Sub
    End Class
End Namespace
