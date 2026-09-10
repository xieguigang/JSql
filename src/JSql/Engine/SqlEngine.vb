Imports System.Collections.Generic
Imports JSql.Indexing
Imports JSql.Sql
Imports JSql.Storage

Namespace Engine

    ''' <summary>
    ''' the sql engine facade: sql text -> ast -> result set,
    ''' owns the database catalog and the search index manager.
    ''' </summary>
    Public Class SqlEngine

        Public ReadOnly Property Catalog As DatabaseCatalog
        Public ReadOnly Property Indexes As IndexManager

        Private ReadOnly executor As SqlExecutor

        Sub New(root As String)
            Catalog = New DatabaseCatalog(root)
            Indexes = New IndexManager(Catalog)
            executor = New SqlExecutor(Me)
        End Sub

        ''' <summary>
        ''' parse and run one sql statement, throws <see cref="SqlError"/> on failure.
        ''' </summary>
        Public Function Execute(statementText As String) As ResultSet
            If String.IsNullOrWhiteSpace(statementText) Then
                Throw New SqlError("empty sql statement!")
            End If

            Return ExecuteStatement(New SqlParser(statementText).ParseStatement())
        End Function

        Public Function ExecuteStatement(stmt As SqlStatement) As ResultSet
            If TypeOf stmt Is SelectStatement Then
                Return executor.ExecuteSelect(DirectCast(stmt, SelectStatement))
            ElseIf TypeOf stmt Is InsertStatement Then
                Return executor.ExecuteInsert(DirectCast(stmt, InsertStatement))
            ElseIf TypeOf stmt Is UpdateStatement Then
                Return executor.ExecuteUpdate(DirectCast(stmt, UpdateStatement))
            ElseIf TypeOf stmt Is DeleteStatement Then
                Return executor.ExecuteDelete(DirectCast(stmt, DeleteStatement))
            ElseIf TypeOf stmt Is CreateStatement Then
                Return executor.ExecuteCreate(DirectCast(stmt, CreateStatement))
            ElseIf TypeOf stmt Is DropStatement Then
                Return executor.ExecuteDrop(DirectCast(stmt, DropStatement))
            ElseIf TypeOf stmt Is UseStatement Then
                Return executor.ExecuteUse(DirectCast(stmt, UseStatement))
            ElseIf TypeOf stmt Is ShowStatement Then
                Return executor.ExecuteShow(DirectCast(stmt, ShowStatement))
            End If

            Throw New SqlError("unsupported sql statement: " & stmt.GetType().Name)
        End Function

        ''' <summary>
        ''' run a batch of statements, stops at the first failure.
        ''' </summary>
        Public Function ExecuteBatch(script As String) As List(Of ResultSet)
            Dim results As New List(Of ResultSet)

            For Each statementText In SplitStatements(script)
                If String.IsNullOrWhiteSpace(statementText) Then
                    Continue For
                End If

                results.Add(Execute(statementText))
            Next

            Return results
        End Function

        Public Shared Iterator Function SplitStatements(script As String) As IEnumerable(Of String)
            Dim sb As New Text.StringBuilder
            Dim quote As Char = ChrW(0)

            For i As Integer = 0 To script.Length - 1
                Dim c As Char = script(i)

                If quote <> ChrW(0) Then
                    sb.Append(c)

                    If c = quote Then
                        quote = ChrW(0)
                    End If

                    Continue For
                End If

                If c = "'"c OrElse c = """"c Then
                    quote = c
                    sb.Append(c)
                ElseIf c = ";"c Then
                    Yield sb.ToString()

                    sb.Clear()
                Else
                    sb.Append(c)
                End If
            Next

            Dim tail As String = sb.ToString()

            If tail.Trim().Length > 0 Then
                Yield tail
            End If
        End Function
    End Class
End Namespace
