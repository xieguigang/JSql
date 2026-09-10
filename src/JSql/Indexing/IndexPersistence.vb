Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports LINQ

Namespace Indexing

    ''' <summary>
    ''' persisted form of one column index. index files are stored beside the
    ''' table data inside the .indexes sub folder of the database directory:
    ''' &lt;db&gt;/.indexes/&lt;table&gt;.&lt;column&gt;.&lt;kind&gt;.idx
    ''' </summary>
    ''' <remarks>
    ''' the term hash index archive keeps the real index maps, the range index can
    ''' not be serialized (its eval delegate), so its raw column data snapshot is
    ''' archived and re-indexed when the archive is loaded back.
    ''' </remarks>
    Public Class IndexArchive

        Public Property version As Integer = 1
        Public Property name As String
        Public Property table As String
        Public Property column As String
        Public Property kind As String
        Public Property valueType As String
        Public Property rowCount As Integer
        ''' <summary>term hash index: term -> document id list</summary>
        Public Property hashMaps As Dictionary(Of String, Integer())
        ''' <summary>term hash index: document id -> row offset</summary>
        Public Property documentMaps As Dictionary(Of Integer, Integer)
        ''' <summary>the indexed document texts of the column, for append-only rebuilds</summary>
        Public Property documents As List(Of String)
    End Class

    Public Class IndexPersistence

        Private Shared ReadOnly jsonOptions As New JsonSerializerOptions With {
            .WriteIndented = False
        }

        Public Shared Function IndexDir(dbDir As String) As String
            Return Path.Combine(dbDir, ".indexes")
        End Function

        Public Shared Function GetPath(dbDir As String, table As String, column As String, kind As String) As String
            Return Path.Combine(IndexDir(dbDir), Safe(table) & "_" & Safe(column) & "_" & kind.ToLower() & ".idx")
        End Function

        Private Shared Function Safe(name As String) As String
            Dim sb As New StringBuilder

            For Each c In name
                If Char.IsLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c Then
                    sb.Append(c)
                Else
                    sb.Append("_"c)
                End If
            Next

            Return sb.ToString().ToLower()
        End Function

        ''' <summary>
        ''' load every index archive that belongs to one table.
        ''' </summary>
        Public Shared Function LoadTableArchives(dbDir As String, table As String) As List(Of IndexArchive)
            Dim list As New List(Of IndexArchive)
            Dim dir As String = IndexDir(dbDir)

            If Not Directory.Exists(dir) Then
                Return list
            End If

            Dim suffix As String = "_" & Safe(table) & "_"

            For Each file As String In System.IO.Directory.GetFiles(dir, "*.idx")
                Dim archive As IndexArchive = Nothing

                Try
                    archive = JsonSerializer.Deserialize(Of IndexArchive)(System.IO.File.ReadAllText(file), jsonOptions)
                Catch ex As Exception
                    Console.Error.WriteLine("[index] broken index file skipped: " & file & " -> " & ex.Message)
                    Continue For
                End Try

                If archive IsNot Nothing AndAlso archive.table IsNot Nothing AndAlso
                   String.Equals(archive.table, table, StringComparison.OrdinalIgnoreCase) Then
                    list.Add(archive)
                End If
            Next

            Return list
        End Function

        Public Shared Function LoadAllArchives(dbDir As String) As List(Of IndexArchive)
            Dim list As New List(Of IndexArchive)
            Dim dir As String = IndexDir(dbDir)

            If Not Directory.Exists(dir) Then
                Return list
            End If

            For Each file As String In System.IO.Directory.GetFiles(dir, "*.idx")
                Try
                    list.Add(JsonSerializer.Deserialize(Of IndexArchive)(System.IO.File.ReadAllText(file), jsonOptions))
                Catch ex As Exception
                    Console.Error.WriteLine("[index] broken index file skipped: " & file & " -> " & ex.Message)
                End Try
            Next

            Return list
        End Function

        ''' <summary>
        ''' write one index archive into the .indexes folder of the given database.
        ''' </summary>
        Public Shared Function Save(archive As IndexArchive, dbDir As String) As String
            Dim dir As String = IndexDir(dbDir)

            If Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            Dim json As String = JsonSerializer.Serialize(archive, jsonOptions)
            Dim file As String = GetPath(dbDir, archive.table, archive.column, archive.kind)

            System.IO.File.WriteAllText(file, json, New UTF8Encoding(False))
            Return file
        End Function

        Public Shared Sub DropTable(dbDir As String, table As String)
            Dim dir As String = IndexDir(dbDir)

            If Not Directory.Exists(dir) Then
                Return
            End If

            For Each file As String In System.IO.Directory.GetFiles(dir, "*.idx")
                Try
                    Dim archive = JsonSerializer.Deserialize(Of IndexArchive)(System.IO.File.ReadAllText(file), jsonOptions)

                    If archive IsNot Nothing AndAlso String.Equals(archive.table, table, StringComparison.OrdinalIgnoreCase) Then
                        System.IO.File.Delete(file)
                    End If
                Catch ex As Exception
                    System.IO.File.Delete(file)
                End Try
            Next
        End Sub

        Public Shared Function DropOne(dbDir As String, table As String, name As String) As Boolean
            Dim dir As String = IndexDir(dbDir)

            If Not Directory.Exists(dir) Then
                Return False
            End If

            For Each file As String In System.IO.Directory.GetFiles(dir, "*.idx")
                Try
                    Dim archive = JsonSerializer.Deserialize(Of IndexArchive)(System.IO.File.ReadAllText(file), jsonOptions)

                    If archive IsNot Nothing AndAlso
                       String.Equals(archive.name, name, StringComparison.OrdinalIgnoreCase) AndAlso
                       String.Equals(archive.table, table, StringComparison.OrdinalIgnoreCase) Then
                        System.IO.File.Delete(file)
                        Return True
                    End If
                Catch ex As Exception
                    ' ignore broken archive files
                End Try
            Next

            Return False
        End Function
    End Class
End Namespace
