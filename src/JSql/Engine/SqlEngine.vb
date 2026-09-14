Imports JSql.Indexing
Imports JSql.Sql
Imports JSql.Storage

Namespace Engine

    ''' <summary>
    ''' the sql engine facade: sql text -> ast -> result set,
    ''' owns the database catalog and the search index manager.
    ''' </summary>
    Public Class SqlEngine : Implements IDisposable

        ''' <summary>当前物理存储后端，可随时通过 <see cref="SetStorage"/> 切换。</summary>
        Public ReadOnly Property DataStore As IDbFileStorageProvider
            Get
                Return _dataStore
            End Get
        End Property

        ''' <summary><see cref="DataStore"/> 的向后兼容别名。</summary>
        Public ReadOnly Property Catalog As IDbFileStorageProvider
            Get
                Return _dataStore
            End Get
        End Property

        Public ReadOnly Property Indexes As IndexManager
            Get
                Return _indexes
            End Get
        End Property

        Public ReadOnly Property Storage As StorageOptions

        Public ReadOnly Property Sessions As TableSessionPool
            Get
                Return _dataStore.Sessions
            End Get
        End Property

        Public ReadOnly Property CheckpointScheduler As IdleMergeScheduler
            Get
                Return _scheduler
            End Get
        End Property

        Private ReadOnly executor As SqlExecutor
        Private _dataStore As IDbFileStorageProvider
        Private _indexes As IndexManager
        Private _scheduler As IdleMergeScheduler

        Sub New(root As String, Optional options As StorageOptions = Nothing)
            Storage = If(options, New StorageOptions())
            executor = New SqlExecutor(Me)
            SetStorage(New TextFileStorage(root, Storage))
        End Sub

        ''' <summary>
        ''' 使用指定的存储后端构造引擎。宿主可以传入 TextFileStorage（默认）或
        ''' SqliteStorage 等实现来切换物理文件引擎。
        ''' </summary>
        Sub New(provider As IDbFileStorageProvider, Optional options As StorageOptions = Nothing)
            Storage = If(options, New StorageOptions())
            executor = New SqlExecutor(Me)
            SetStorage(provider)
        End Sub

        ''' <summary>
        ''' 切换物理存储后端：先释放旧后端（触发其 checkpoint / 提交），再装配新的
        ''' 表会话池、索引管理器与空闲合并调度器。
        ''' </summary>
        Public Sub SetStorage(provider As IDbFileStorageProvider)
            If provider Is Nothing Then Throw New ArgumentNullException(NameOf(provider))
            If _dataStore Is provider Then Return

            DisposeBackend()

            _dataStore = provider
            _indexes = New IndexManager(provider)
            _scheduler = New IdleMergeScheduler(provider.Sessions, Storage)

            If Not Storage.MultiProcessAccess Then
                ' 多进程模式下每条语句结束即释放锁；后台空闲合并会一直持有会话（＝持有锁），
                ' 因此必须停用它。数据由「语句结束时的合并」与退出流程保证落盘。
                _scheduler.Start()
            End If
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
                ReleaseStatementScope()
                CheckpointScheduler.ExitBusy()
            End Try
        End Function

        ''' <summary>
        ''' 结束一次语句作用域。多进程访问模式下释放所有表级文件锁，使其它进程可以
        ''' 取得锁执行自己的语句；同时失效查询索引的内存缓存，避免另一进程修改数据后
        ''' 索引候选集漏行。单进程模式下为空操作（行为与旧版一致）。
        ''' <para>
        ''' 直接调用 <see cref="ExecuteStatement"/> 的调用方也应在本语句结束后调用本方法。
        ''' </para>
        ''' </summary>
        Public Sub ReleaseStatementScope()
            If Not Storage.MultiProcessAccess Then
                Return
            End If

            Sessions.ReleaseAll(Storage.MergeOnStatementEnd)

            If _indexes IsNot Nothing Then
                _indexes.InvalidateAll()
            End If

            If _scheduler IsNot Nothing Then
                _scheduler.Touch()
            End If
        End Sub

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
            If _scheduler IsNot Nothing Then
                _scheduler.Touch()
            End If

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
            DisposeBackend()
        End Sub

        ''' <summary>释放当前后端：停止调度器、合并/提交未落盘的修改并关闭会话池。</summary>
        Private Sub DisposeBackend()
            If _scheduler IsNot Nothing Then
                Try
                    _scheduler.Stop()
                    _scheduler.Dispose()
                Catch
                    ' the shutdown path must never throw
                End Try

                _scheduler = Nothing
            End If

            _indexes = Nothing

            If _dataStore IsNot Nothing Then
                Try
                    _dataStore.Dispose()
                Catch
                    ' the shutdown path must never throw
                End Try

                _dataStore = Nothing
            End If
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
