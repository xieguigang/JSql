Imports Microsoft.VisualBasic.Data.Repository

Namespace Storage

    ''' <summary>新建表所使用的物理存储格式。</summary>
    Public Enum StorageFormat
        ''' <summary>一行一个 JSON 对象的 JSONL 数据文件（默认）。</summary>
        Jsonl = 0
        ''' <summary>表头行 + 数据行的 CSV 数据文件。</summary>
        Csv = 1
    End Enum

    ''' <summary>锁冲突（另一个进程正在使用该表）时的处理策略。</summary>
    Public Enum LockConflictPolicy
        ''' <summary>等待并重试，直到超时后报错（默认）。</summary>
        Wait = 0
        ''' <summary>立即失败并抛出明确错误，由调用方自行重试。</summary>
        FailFast = 1
    End Enum

    ''' <summary>
    ''' runtime switches of the table storage layer. the jsonl layout is the
    ''' default, the legacy whole table json file is kept for compatibility.
    ''' </summary>
    Public Class StorageOptions

        ''' <summary>
        ''' how long the engine must stay idle before the pending WAL records are
        ''' merged back into the data file. &lt;= 0 disables the background merge.
        ''' </summary>
        Public Property MergeIdleSeconds As Integer = 30

        ''' <summary>
        ''' fsync the WAL on every write. false is much faster but only survives a
        ''' process crash; true also survives a power failure.
        ''' </summary>
        Public Property FsyncEachWrite As Boolean = False

        ''' <summary>merge the pending WAL once the operation count reaches this value</summary>
        Public Property MergeAfterOperations As Integer = 2000

        ''' <summary>write the store diagnostics(index rebuild, torn tail repair) to stderr</summary>
        Public Property Verbose As Boolean = False

        ''' <summary>create new tables as legacy single file json instead of the jsonl layout</summary>
        Public Property LegacyJson As Boolean = False

        ''' <summary>
        ''' 新建表默认使用的物理存储格式。已有的表按其数据文件扩展名自动识别，
        ''' 本选项只影响“尚不存在数据文件的新表”。
        ''' </summary>
        Public Property Format As StorageFormat = StorageFormat.Jsonl

        ''' <summary>sparse line index granularity of the data file</summary>
        Public Property IndexGranularity As Integer = 1024

        ''' <summary>
        ''' 多进程访问模式：开启后每条语句结束即释放表级文件锁，使多个进程可以
        ''' 「交替」在同一个数据库上执行语句（任一时刻仍只有一个写者）。
        ''' 默认关闭：保持单进程独占语义与性能不变。
        ''' <para>
        ''' 仅作用于文本后端（JSONL / CSV）；旧版单文件 json 布局与 SQLite 后端不受影响。
        ''' </para>
        ''' </summary>
        Public Property MultiProcessAccess As Boolean = False

        ''' <summary>进程级锁模式。默认独占；共享读 / 不加锁供只读或高级场景使用。</summary>
        Public Property LockMode As TextStoreLockMode = TextStoreLockMode.Exclusive

        ''' <summary>锁冲突时的处理策略（默认等待重试）。</summary>
        Public Property LockConflictPolicy As LockConflictPolicy = LockConflictPolicy.Wait

        ''' <summary>等待获取锁的超时（毫秒），仅在多进程模式的等待策略下生效。</summary>
        Public Property LockWaitTimeoutMs As Integer = 5000

        ''' <summary>锁冲突后的重试间隔（毫秒）。</summary>
        Public Property LockRetryIntervalMs As Integer = 50

        ''' <summary>
        ''' 语句结束释放锁时是否把未合并的 WAL 合并回数据文件。
        ''' 开启（默认）可让磁盘保持最新、避免下一条语句重放越来越长的日志；
        ''' 关闭则以写入吞吐优先（WAL 会增长，数据由下次打开时重放保证不丢）。
        ''' </summary>
        Public Property MergeOnStatementEnd As Boolean = True

        ''' <summary>build the options object handed over to the format-agnostic text line store engine</summary>
        Public Function CreateStoreOptions() As TextStoreOptions
            ' 单进程模式保持旧行为：锁冲突立即失败（超时 0）。
            ' 多进程模式 + 等待策略才启用超时重试。
            Dim lockTimeout As Integer = 0

            If MultiProcessAccess AndAlso LockConflictPolicy = LockConflictPolicy.Wait Then
                lockTimeout = Math.Max(0, LockWaitTimeoutMs)
            End If

            Return New TextStoreOptions With {
                .IndexGranularity = IndexGranularity,
                .FsyncEachWrite = FsyncEachWrite,
                .RepairTornTail = True,
                .LockMode = LockMode,
                .LockWaitTimeoutMs = lockTimeout,
                .LockRetryIntervalMs = Math.Max(1, LockRetryIntervalMs)
            }
        End Function
    End Class
End Namespace
