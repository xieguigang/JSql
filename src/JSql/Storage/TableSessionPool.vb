Namespace Storage

    ''' <summary>
    ''' caches the open table sessions. a row data file is exclusively locked by the
    ''' store engine, so one table may only be opened once per process; every write
    ''' statement reuses the session of the previous statement. sessions are created
    ''' lazily through a factory delegate, so the pool stays format agnostic.
    ''' </summary>
    Public Class TableSessionPool : Implements IDisposable

        Private ReadOnly _options As StorageOptions
        Private ReadOnly _sessions As New Dictionary(Of String, ITableSession)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _gate As New Object

        ''' <summary>the outcome of the last checkpoint, used by the scheduler</summary>
        Private _lastMergeErrors As Integer
        Private _lastMergePostponed As Integer

        ''' <summary>raised when a session reports a diagnostic message</summary>
        Public Event Info(message As String)

        Sub New(options As StorageOptions)
            _options = options
        End Sub

        Public ReadOnly Property Options As StorageOptions
            Get
                Return _options
            End Get
        End Property

        Private Shared Function CacheKey(dbDir As String, table As String) As String
            Return dbDir.ToLowerInvariant() & "/" & table
        End Function

        ''' <summary>get the session of a table, open it through the factory on the first access</summary>
        Public Function GetOrOpen(dbDir As String, table As String, creator As Func(Of ITableSession)) As ITableSession
            If creator Is Nothing Then Throw New ArgumentNullException(NameOf(creator))

            Dim key As String = CacheKey(dbDir, table)

            SyncLock _gate
                Dim session As ITableSession = Nothing

                If _sessions.TryGetValue(key, session) Then
                    Return session
                End If

                session = creator()
                AddHandler session.Info, AddressOf OnSessionInfo
                _sessions(key) = session
                Return session
            End SyncLock
        End Function

        Public Function TryGet(dbDir As String, table As String) As ITableSession
            SyncLock _gate
                Dim session As ITableSession = Nothing
                _sessions.TryGetValue(CacheKey(dbDir, table), session)
                Return session
            End SyncLock
        End Function

        Public Function OpenSessions() As List(Of ITableSession)
            SyncLock _gate
                Return _sessions.Values.ToList()
            End SyncLock
        End Function

        ''' <summary>snapshot of the open sessions together with their cache keys</summary>
        Public Function OpenSessionKeys() As List(Of KeyValuePair(Of String, ITableSession))
            Dim list As New List(Of KeyValuePair(Of String, ITableSession))

            SyncLock _gate
                For Each kv In _sessions
                    list.Add(New KeyValuePair(Of String, ITableSession)(kv.Key, kv.Value))
                Next
            End SyncLock

            Return list
        End Function

        Private Sub OnSessionInfo(message As String)
            RaiseEvent Info(message)
        End Sub

        ''' <summary>
        ''' close one session and release its exclusive lock. the caller is
        ''' responsible for deleting the files afterwards.
        ''' </summary>
        Public Sub Close(dbDir As String, table As String)
            Dim key As String = CacheKey(dbDir, table)
            Dim session As ITableSession = Nothing

            SyncLock _gate
                If _sessions.TryGetValue(key, session) Then
                    _sessions.Remove(key)
                End If
            End SyncLock

            If session IsNot Nothing Then
                RemoveHandler session.Info, AddressOf OnSessionInfo
                session.Dispose()
            End If
        End Sub

        Public Sub CloseDatabase(dbDir As String)
            Dim prefix As String = dbDir.ToLowerInvariant() & "/"
            Dim keys As New List(Of String)

            SyncLock _gate
                For Each key In _sessions.Keys
                    If key.StartsWith(prefix, StringComparison.Ordinal) Then
                        keys.Add(key)
                    End If
                Next
            End SyncLock

            For Each key As String In keys
                Dim session As ITableSession = Nothing

                SyncLock _gate
                    If _sessions.TryGetValue(key, session) Then
                        _sessions.Remove(key)
                    End If
                End SyncLock

                If session IsNot Nothing Then
                    RemoveHandler session.Info, AddressOf OnSessionInfo
                    session.Dispose()
                End If
            Next
        End Sub

        ''' <summary>how many tables failed to merge during the last checkpoint</summary>
        Public ReadOnly Property LastMergeErrors As Integer
            Get
                Return _lastMergeErrors
            End Get
        End Property

        ''' <summary>how many tables postponed their merge during the last checkpoint</summary>
        Public ReadOnly Property LastMergePostponed As Integer
            Get
                Return _lastMergePostponed
            End Get
        End Property

        ''' <summary>
        ''' merge the pending write ahead log of every open session. returns the
        ''' number of merged tables. a table which could not be merged right now (a
        ''' read enumeration is still active) is postponed, so that the caller is
        ''' able to retry it on the next checkpoint instead of treating it as done.
        ''' </summary>
        Public Function MergeAll(Optional force As Boolean = False) As Integer
            Dim merged As Integer = 0
            Dim errors As Integer = 0
            Dim postponed As Integer = 0

            For Each session As ITableSession In OpenSessions()
                Try
                    If force OrElse session.HasPendingChanges Then
                        If session.TryMerge() Then
                            merged += 1
                        Else
                            postponed += 1
                        End If
                    End If
                Catch ex As Exception
                    errors += 1
                    RaiseError("merge failed for " & session.TableName & ": " & ex.Message)
                End Try
            Next

            _lastMergeErrors = errors
            _lastMergePostponed = postponed

            If postponed > 0 Then
                RaiseEvent Info("merge postponed for " & postponed & " table(s)")
            End If

            Return merged
        End Function

        ''' <summary>merge the tables whose pending operation count exceeds the threshold</summary>
        Public Function MergeOverloaded(threshold As Integer) As Integer
            Dim merged As Integer = 0
            Dim errors As Integer = 0
            Dim postponed As Integer = 0

            For Each session As ITableSession In OpenSessions()
                Try
                    If session.PendingOperations >= threshold AndAlso session.HasPendingChanges Then
                        If session.TryMerge() Then
                            merged += 1
                        Else
                            postponed += 1
                        End If
                    End If
                Catch ex As Exception
                    errors += 1
                    RaiseError("merge failed for " & session.TableName & ": " & ex.Message)
                End Try
            Next

            _lastMergeErrors += errors
            _lastMergePostponed += postponed

            Return merged
        End Function

        ''' <summary>
        ''' a checkpoint problem must never be silent: it is published through the
        ''' <see cref="Info"/> event and written to stderr when nobody listens, so
        ''' that a stuck write ahead log is always visible in the server log.
        ''' </summary>
        Private Sub RaiseError(message As String)
            RaiseEvent Info(message)

            Try
                Console.Error.WriteLine("[jsql] " & message)
            Catch
                ' the diagnostics must never break the checkpoint itself
            End Try
        End Sub

        ''' <summary>
        ''' 释放所有已打开的表会话（＝释放它们的文件锁），使其它进程可以取得锁执行语句。
        ''' <para>
        ''' <paramref name="merge"/> = True（默认）时先把待合并的 WAL 合并回数据文件，
        ''' 让磁盘保持最新并避免下一条语句重放越来越长的日志；False 时只 flush 日志
        ''' 并释放锁，未合并的记录保留在 WAL 中（下次打开时重放，崩溃恢复语义不变）。
        ''' </para>
        ''' <para>
        ''' 与 <see cref="DisposeAll"/> 不同，本方法可重复调用，池在调用后仍然可用。
        ''' </para>
        ''' </summary>
        Public Sub ReleaseAll(Optional merge As Boolean = True)
            Dim sessions As List(Of ITableSession) = OpenSessions()

            SyncLock _gate
                _sessions.Clear()
            End SyncLock

            For Each session As ITableSession In sessions
                RemoveHandler session.Info, AddressOf OnSessionInfo

                Try
                    If merge Then
                        If session.HasPendingChanges Then
                            session.Merge()
                        End If
                    Else
                        ' 只保证日志落盘；挂起的修改留给下次打开时重放
                        session.Flush()
                    End If
                Catch ex As Exception
                    ' 合并/刷盘失败不应阻塞锁的释放：未合并的记录仍保留在 WAL 中，下次打开会重放
                    RaiseEvent Info("release failed for " & session.TableName & ": " & ex.Message)
                End Try

                Try
                    session.Dispose()
                Catch ex As Exception
                    RaiseEvent Info("dispose failed for " & session.TableName & ": " & ex.Message)
                End Try
            Next
        End Sub

        Public Sub DisposeAll()
            Dim sessions As List(Of ITableSession) = OpenSessions()

            SyncLock _gate
                _sessions.Clear()
            End SyncLock

            For Each session As ITableSession In sessions
                RemoveHandler session.Info, AddressOf OnSessionInfo

                Try
                    If session.HasPendingChanges Then
                        Call session.TryMerge()
                    End If
                Catch
                    ' a broken merge must not block the shutdown
                End Try

                session.Dispose()
            Next
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            DisposeAll()
        End Sub
    End Class
End Namespace
