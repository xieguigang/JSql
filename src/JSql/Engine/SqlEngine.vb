Imports JSql.Indexing
Imports JSql.Sql
Imports JSql.Storage

Namespace Engine

    ''' <summary>
    ''' the sql engine facade: sql text -> ast -> result set,
    ''' owns the database catalog and the search index manager.
    ''' </summary>
    Public Class SqlEngine : Implements IDisposable

        Public ReadOnly Property Catalog As DatabaseCatalog
        Public ReadOnly Property Indexes As IndexManager
        Public ReadOnly Property Storage As StorageOptions
        Public ReadOnly Property Sessions As TableSessionPool
        Public ReadOnly Property CheckpointScheduler As IdleMergeScheduler

        Private ReadOnly executor As SqlExecutor

        Sub New(root As String, Optional options As StorageOptions = Nothing)
            Storage = If(options, New StorageOptions())
            Catalog = New DatabaseCatalog(root, Storage)
            Sessions = Catalog.Sessions
            Indexes = New IndexManager(Catalog)
            executor = New SqlExecutor(Me)
            CheckpointScheduler = New IdleMergeScheduler(Sessions, Storage)
            CheckpointScheduler.Start()
        End Sub

        ''' <summary>
        ''' parse and run one sql statement, throws <see cref="SqlError"/> on failure.
        ''' the statement is executed with the background checkpoint paused.
        ''' </summary>
        Public Function Execute(statementText As String) As ResultSet
            If String.IsNullOrWhiteSpace(statementText) Then
                Throw New SqlError("empty sql statement!")
            End If

            CheckpointScheduler.EnterBusy()

            Try
                Return ExecuteStatement(New SqlParser(statementText).ParseStatement())
            Finally
                CheckpointScheduler.ExitBusy()
            End Try
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
            ElseIf TypeOf stmt Is CheckpointStatement Then
                Return executor.ExecuteCheckpoint(DirectCast(stmt, CheckpointStatement))
            End If

            Throw New SqlError("unsupported sql statement: " & stmt.GetType().Name)
        End Function

        ''' <summary>
        ''' merge the pending write ahead log back into the data files. called by the
        ''' idle scheduler and on shutdown, also reachable through CHECKPOINT.
        ''' </summary>
        Public Function MergeAll(Optional force As Boolean = True) As Integer
            CheckpointScheduler.Touch()
            Return Sessions.MergeAll(force)
        End Function

        ''' <summary>merge one table, the session is opened first when needed</summary>
        Public Function MergeTable(db As String, table As String) As Boolean
            Dim session As ITableSession = Catalog.TryGetSession(db, table)

            If session Is Nothing Then
                If Catalog.IsLegacyTable(db, table) Then
                    Return False
                End If

                session = Catalog.OpenSession(db, table)
            End If

            session.Merge()
            Return True
        End Function

        ''' <summary>flush the write ahead log of every open session</summary>
        Public Sub FlushAll()
            For Each session As ITableSession In Sessions.OpenSessions()
                Try
                    session.Flush()
                Catch
                    ' flushing must never break the shutdown path
                End Try
            Next
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            CheckpointScheduler.Stop()
            CheckpointScheduler.Dispose()
            Catalog.Dispose()
        End Sub

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
