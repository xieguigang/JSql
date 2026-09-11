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

        Public Event Info(message As String)

        Sub New(pool As TableSessionPool, options As StorageOptions)
            _pool = pool
            _options = options
            _lastActivity = Date.UtcNow
            _lastMerge = Date.MinValue
            _timer = New Timer(AddressOf OnTick, Nothing, Timeout.Infinite, Timeout.Infinite)
        End Sub

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

        Public Sub Start()
            If _disposed OrElse _options.MergeIdleSeconds <= 0 Then
                Return
            End If

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
        End Sub

        Private Sub OnTick(state As Object)
            If _disposed Then
                Return
            End If

            ' never run two checkpoints at the same time
            If Interlocked.CompareExchange(_merging, 1, 0) <> 0 Then
                Return
            End If

            Try
                If _busy > 0 Then
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

                Dim idleSeconds As Double = IdleSeconds

                If idleSeconds < _options.MergeIdleSeconds Then
                    Return
                End If

                Dim merged As Integer = _pool.MergeAll()

                If merged > 0 Then
                    _lastMerge = Date.UtcNow
                    RaiseEvent Info("checkpoint: merged " & merged & " table(s) after " &
                                    CInt(idleSeconds) & "s idle")
                Else
                    ' nothing to merge, the next idle window starts over
                    Touch()
                End If
            Catch ex As Exception
                RaiseEvent Info("checkpoint failed: " & ex.Message)
            Finally
                Interlocked.Exchange(_merging, 0)
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _disposed = True
            _timer.Dispose()
        End Sub
    End Class
End Namespace
