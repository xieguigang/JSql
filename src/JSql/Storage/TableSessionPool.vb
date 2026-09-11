Namespace Storage

    ''' <summary>
    ''' caches the open table sessions. a jsonl data file is exclusively locked by
    ''' the store engine, so one table may only be opened once per process; every
    ''' write statement reuses the session of the previous statement.
    ''' </summary>
    Public Class TableSessionPool : Implements IDisposable

        Private ReadOnly _options As StorageOptions
        Private ReadOnly _sessions As New Dictionary(Of String, JsonlTableSession)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _gate As New Object

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

        ''' <summary>get the session of a table, open it on the first access</summary>
        Public Function GetOrOpen(dbDir As String, table As String, schema As TableSchema,
                                 schemaPath As String, dataPath As String) As JsonlTableSession
            Dim key As String = CacheKey(dbDir, table)

            SyncLock _gate
                Dim session As JsonlTableSession = Nothing

                If _sessions.TryGetValue(key, session) Then
                    Return session
                End If

                session = New JsonlTableSession(schema, schemaPath, dataPath, _options)
                AddHandler session.Info, AddressOf OnSessionInfo
                _sessions(key) = session
                Return session
            End SyncLock
        End Function

        Public Function TryGet(dbDir As String, table As String) As JsonlTableSession
            SyncLock _gate
                Dim session As JsonlTableSession = Nothing
                _sessions.TryGetValue(CacheKey(dbDir, table), session)
                Return session
            End SyncLock
        End Function

        Public Function OpenSessions() As List(Of JsonlTableSession)
            SyncLock _gate
                Return _sessions.Values.ToList()
            End SyncLock
        End Function

        ''' <summary>snapshot of the open sessions together with their cache keys</summary>
        Public Function OpenSessionKeys() As List(Of KeyValuePair(Of String, JsonlTableSession))
            Dim list As New List(Of KeyValuePair(Of String, JsonlTableSession))

            SyncLock _gate
                For Each kv In _sessions
                    list.Add(New KeyValuePair(Of String, JsonlTableSession)(kv.Key, kv.Value))
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
            Dim session As JsonlTableSession = Nothing

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

            For Each key In keys
                Dim session As JsonlTableSession = Nothing

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

        ''' <summary>
        ''' merge the pending write ahead log of every open session. returns the
        ''' number of merged tables.
        ''' </summary>
        Public Function MergeAll(Optional force As Boolean = False) As Integer
            Dim merged As Integer = 0

            For Each session As JsonlTableSession In OpenSessions()
                Try
                    If force OrElse session.HasPendingChanges Then
                        session.Merge()
                        merged += 1
                    End If
                Catch ex As Exception
                    RaiseEvent Info("merge failed for " & session.TableName & ": " & ex.Message)
                End Try
            Next

            Return merged
        End Function

        ''' <summary>merge the tables whose pending operation count exceeds the threshold</summary>
        Public Function MergeOverloaded(threshold As Integer) As Integer
            Dim merged As Integer = 0

            For Each session As JsonlTableSession In OpenSessions()
                Try
                    If session.PendingOperations >= threshold AndAlso session.HasPendingChanges Then
                        session.Merge()
                        merged += 1
                    End If
                Catch ex As Exception
                    RaiseEvent Info("merge failed for " & session.TableName & ": " & ex.Message)
                End Try
            Next

            Return merged
        End Function

        Public Sub DisposeAll()
            Dim sessions As List(Of JsonlTableSession) = OpenSessions()

            SyncLock _gate
                _sessions.Clear()
            End SyncLock

            For Each session In sessions
                RemoveHandler session.Info, AddressOf OnSessionInfo

                Try
                    If session.HasPendingChanges Then
                        session.Merge()
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
