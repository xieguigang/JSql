Imports System.IO

Namespace Storage

    ''' <summary>
    ''' manages the database root folder: one sub-folder is one database, table
    ''' files inside are managed through the <see cref="ITableStore"/> abstraction.
    ''' </summary>
    Public Class DatabaseCatalog

        Public Shared ReadOnly InvalidNameChars As Char() = {"/"c, "\"c, ":"c, "*"c, "?"c, """"c, "<"c, ">"c, "|"c}

        Public ReadOnly Property Root As String
        Public Property CurrentDatabase As String

        Sub New(root As String)
            Me.Root = Path.GetFullPath(root)
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
            If Directory.Exists(dir) Then
                Directory.Delete(dir, recursive:=True)
            End If
        End Sub

        Public Function DatabaseDir(db As String) As String
            ValidateName(db, "database")
            Return Path.Combine(Root, db)
        End Function

        Public Function FindTableFile(db As String, table As String) As String
            Dim dir As String = DatabaseDir(db)
            If Not Directory.Exists(dir) Then
                Return Nothing
            End If

            For Each ext As String In StorageFactory.GetSupportedExtensions()
                Dim filePath As String = Path.Combine(dir, table & ext)
                If File.Exists(filePath) Then
                    Return filePath
                End If
            Next

            Return Nothing
        End Function

        Public Function TableExists(db As String, table As String) As Boolean
            Return FindTableFile(db, table) IsNot Nothing
        End Function

        Public Function GetTables(db As String) As List(Of String)
            Dim dir As String = DatabaseDir(db)
            Dim names As New List(Of String)

            If Not Directory.Exists(dir) Then
                Return names
            End If

            Dim exts As New HashSet(Of String)(StorageFactory.GetSupportedExtensions(), StringComparer.OrdinalIgnoreCase)

            For Each file As String In Directory.GetFiles(dir)
                If exts.Contains(Path.GetExtension(file)) Then
                    names.Add(Path.GetFileNameWithoutExtension(file))
                End If
            Next

            names.Sort(StringComparer.OrdinalIgnoreCase)
            Return names
        End Function

        ''' <summary>
        ''' load a table by dispatching to the right <see cref="ITableStore"/> implementation.
        ''' </summary>
        Public Function LoadTable(db As String, table As String) As StoredTable
            Dim filePath As String = FindTableFile(db, table)

            If filePath Is Nothing Then
                Throw New ArgumentException($"table '{table}' not found in database '{db}'!")
            End If

            Return StorageFactory.GetStore(filePath).Read(filePath)
        End Function

        ''' <summary>
        ''' write a table back using its own storage format, in an atomic manner.
        ''' </summary>
        Public Sub SaveTable(db As String, table As StoredTable)
            Dim filePath As String = FindTableFile(db, table.Schema.TableName)

            If filePath Is Nothing Then
                filePath = Path.Combine(DatabaseDir(db), table.Schema.TableName & StorageFactory.GetDefaultStore().FileExtension)
            End If

            StorageFactory.GetStore(filePath).Write(filePath, table)
        End Sub

        ''' <summary>delete the table file of a dropped table</summary>
        Public Sub DeleteTable(db As String, table As String)
            Dim filePath As String = FindTableFile(db, table)

            If filePath IsNot Nothing AndAlso File.Exists(filePath) Then
                File.Delete(filePath)
            End If
        End Sub
    End Class
End Namespace
