Imports System.Threading

Namespace Storage

    ''' <summary>
    ''' periodically merges the pending write ahead logs back into the data files
    ''' while the engine is idle. the timer never runs while a sql statement is
    ''' being executed and never overlaps with itself.
    ''' </summary>
    Public Class IdleMergeScheduler : Implements IDisposable

        Private ReadOnly _pool As TableSessionPool
        Private ReadOnly _options As StorageOptions
        Private ReadOnly _timer As Timer

        Private _busy As Integer
        Private _merging As Integer
        Private _lastActivity As Date
        Private _lastMerge As Date
        Private _disposed As Boolean

        ''' <summary>the number of the timer callbacks which have been executed</summary>
        Private _ticks As Integer
        ''' <summary>true when the periodic timer has actually been armed</summary>
        Private _running As Boolean
        ''' <summary>the last exception of the merge loop, kept for diagnostics</summary>
        Private _lastError As String

        ''' <summary>diagnostic counters of the early return paths of <see cref="OnTick"/></summary>
        Private _skipBusy As Integer
        Private _skipMerging As Integer
        Private _skipIdle As Integer
        Private _reachedMerge As Integer
        ''' <summary>the idle window which the last tick observed</summary>
        Private _lastIdleSeen As Double = -1
        ''' <summary>the largest idle window which any tick has observed</summary>
        Private _maxIdleSeen As Double = -1
        ''' <summary>how often the idle countdown has been reset</summary>
        Private _touchCount As Integer

        Public Event Info(message As String)

        Sub New(pool As TableSessionPool, options As StorageOptions)
            _pool = pool
            _options = options
            _lastActivity = Date.UtcNow
            _lastMerge = Date.MinValue
            _timer = New Timer(AddressOf OnTick, Nothing, Timeout.Infinite, Timeout.Infinite)
        End Sub

        ''' <summary>the number of the statements which are currently running</summary>
        Public ReadOnly Property BusyCount As Integer
            Get
                Return _busy
            End Get
        End Property

        Public ReadOnly Property LastMergeTime As Date
            Get
                Return _lastMerge
            End Get
        End Property

        ''' <summary>seconds elapsed since the last sql statement</summary>
        Public ReadOnly Property IdleSeconds As Double
            Get
                Return (Date.UtcNow - _lastActivity).TotalSeconds
            End Get
        End Property

        ''' <summary>the number of the timer callbacks which have been executed</summary>
        Public ReadOnly Property TickCount As Integer
            Get
                Return _ticks
            End Get
        End Property

        ''' <summary>true when the periodic timer has actually been armed</summary>
        Public ReadOnly Property IsRunning As Boolean
            Get
                Return _running
            End Get
        End Property

        ''' <summary>the last exception of the merge loop, kept for diagnostics</summary>
        Public ReadOnly Property LastError As String
            Get
                Return _lastError
            End Get
        End Property

        ''' <summary>how often the checkpoint was skipped because a statement was running</summary>
        Public ReadOnly Property SkippedByBusy As Integer
            Get
                Return _skipBusy
            End Get
        End Property

        ''' <summary>how often the checkpoint was skipped because another one was running</summary>
        Public ReadOnly Property SkippedByMerging As Integer
            Get
                Return _skipMerging
            End Get
        End Property

        ''' <summary>how often the engine was not idle long enough</summary>
        Public ReadOnly Property SkippedByIdle As Integer
            Get
                Return _skipIdle
            End Get
        End Property

        ''' <summary>how often <see cref="TableSessionPool.MergeAll"/> has actually been called</summary>
        Public ReadOnly Property ReachedMerge As Integer
            Get
                Return _reachedMerge
            End Get
        End Property

        ''' <summary>the idle window which the last tick observed</summary>
        Public ReadOnly Property LastIdleSeen As Double
            Get
                Return _lastIdleSeen
            End Get
        End Property

        ''' <summary>how often the idle countdown has been reset</summary>
        Public ReadOnly Property TouchCount As Integer
            Get
                Return _touchCount
            End Get
        End Property

        ''' <summary>a one line diagnostic snapshot of the scheduler state</summary>
        Public Overrides Function ToString() As String
            Return $"running={_running}, ticks={_ticks}, busy={_busy}, merging={_merging}, " &
                   $"idle={IdleSeconds:0.0}s/{_options.MergeIdleSeconds}s, " &
                   $"seen={_lastIdleSeen:0.0}s, maxSeen={_maxIdleSeen:0.0}s, " &
                   $"touches={_touchCount}, " &
                   $"skip(busy={_skipBusy}, merging={_skipMerging}, idle={_skipIdle}), " &
                   $"merge={_reachedMerge}, errors={If(_lastError, "none")}"
        End Function

        Public Sub Start()
            If _disposed OrElse _options.MergeIdleSeconds <= 0 Then
                Return
            End If

            _running = True
            _timer.Change(1000, 1000)
        End Sub

        Public Sub [Stop]()
            If _disposed Then
                Return
            End If

            _timer.Change(Timeout.Infinite, Timeout.Infinite)
        End Sub

        ''' <summary>mark the beginning of a sql statement: checkpoints are paused</summary>
        Public Sub EnterBusy()
            Interlocked.Increment(_busy)
            Touch()
        End Sub

        Public Sub ExitBusy()
            Interlocked.Decrement(_busy)
            Touch()
        End Sub

        ''' <summary>reset the idle countdown</summary>
        Public Sub Touch()
            _lastActivity = Date.UtcNow
            Interlocked.Increment(_touchCount)
        End Sub

        Private Sub OnTick(state As Object)
            Interlocked.Increment(_ticks)

            If _disposed Then
                Return
            End If

            ' never run two checkpoints at the same time
            If Interlocked.CompareExchange(_merging, 1, 0) <> 0 Then
                Interlocked.Increment(_skipMerging)
                Return
            End If

            Try
                If _busy > 0 Then
                    Interlocked.Increment(_skipBusy)
                    Return
                End If

                ' a table with too many pending wal operations is merged even when
                ' the engine is not idle yet
                If _options.MergeAfterOperations > 0 Then
                    Dim overloaded As Integer = _pool.MergeOverloaded(_options.MergeAfterOperations)

                    If overloaded > 0 Then
                        _lastMerge = Date.UtcNow
                        RaiseEvent Info("checkpoint: merged " & overloaded & " overloaded table(s)")
                        Return
                    End If
                End If

                ' NOTE: the local must NOT be named "idleSeconds": visual basic is
                ' case insensitive, so a local with the same name as the property
                ' would shadow it inside its own initializer, the idle window would
                ' always read zero and the checkpoint would never run at all.
                Dim idleElapsed As Double = Me.IdleSeconds

                _lastIdleSeen = idleElapsed
                If idleElapsed > _maxIdleSeen Then _maxIdleSeen = idleElapsed

                If idleElapsed < _options.MergeIdleSeconds Then
                    Interlocked.Increment(_skipIdle)
                    Return
                End If

                Interlocked.Increment(_reachedMerge)

                Dim merged As Integer = _pool.MergeAll()

                If merged > 0 Then
                    _lastMerge = Date.UtcNow
                    RaiseEvent Info("checkpoint: merged " & merged & " table(s) after " &
                                    CInt(idleElapsed) & "s idle")
                    Return
                End If

                If _pool.LastMergeErrors > 0 OrElse _pool.LastMergePostponed > 0 Then
                    ' the tables still have pending work, so the checkpoint is retried
                    ' on the next tick instead of waiting for a whole new idle window.
                    RaiseEvent Info("checkpoint: nothing merged, " &
                                    _pool.LastMergeErrors & " error(s), " &
                                    _pool.LastMergePostponed & " postponed")
                    Return
                End If

                ' nothing to merge, the next idle window starts over
                Touch()
            Catch ex As Exception
                _lastError = ex.Message
                RaiseError("checkpoint failed: " & ex.Message)
            Finally
                Interlocked.Exchange(_merging, 0)
            End Try
        End Sub

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

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
            _timer.Dispose()
        End Sub
    End Class
End Namespace
