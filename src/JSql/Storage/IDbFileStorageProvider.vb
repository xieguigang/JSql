Namespace Storage

    ''' <summary>
    ''' 数据库文件存储后端抽象：一个实现代表一种物理存储引擎，负责管理整库的
    ''' 目录/文件、库内表的枚举与建删、表会话的获取以及表数据的加载/保存/删除。
    ''' <para>
    ''' 默认实现是 <see cref="TextFileStorage"/>（一个文件夹 = 一个数据库，
    ''' 表数据以 JSONL / CSV 文本行存放并带 WAL）；外部实现（例如基于 SQLite
    ''' 数据库文件的引擎）可在宿主构造 <c>SqlEngine</c> 时注入，从而切换物理文件引擎。
    ''' </para>
    ''' </summary>
    Public Interface IDbFileStorageProvider : Inherits IDisposable

        ''' <summary>后端名称，用于展示与诊断，例如 "text" / "sqlite"。</summary>
        ReadOnly Property ProviderName As String

        ''' <summary>数据根目录。一个实现只在其根目录下管理自己格式的库文件/目录。</summary>
        ReadOnly Property Root As String

        ''' <summary>存储层运行开关（合并间隔、fsync、诊断等）。</summary>
        ReadOnly Property Options As StorageOptions

        ''' <summary>与格式无关的表会话缓存池。</summary>
        ReadOnly Property Sessions As TableSessionPool

        ''' <summary>当前选中的数据库（由引擎在 USE 时设置）。</summary>
        Property CurrentDatabase As String

        ' ==================== 库级操作 ====================

        Function DatabaseExists(name As String) As Boolean
        Function GetDatabases() As List(Of String)
        Sub CreateDatabase(name As String)
        Sub DropDatabase(name As String)

        ''' <summary>
        ''' 数据库的辅助目录：用于存放 schema 文件（&lt;表&gt;.schema.json）与列索引
        ''' （.indexes）。文本后端的库目录本身即数据目录；文件式后端可返回独立的辅助目录。
        ''' </summary>
        Function DatabaseDir(db As String) As String

        ' ==================== 表级目录 ====================

        Function TableExists(db As String, table As String) As Boolean
        Function GetTables(db As String) As List(Of String)

        ''' <summary>该表是否使用需要写入迁移的旧版布局（文件式后端恒为 False）。</summary>
        Function IsLegacyTable(db As String, table As String) As Boolean

        ''' <summary>表的主数据文件路径，不存在时返回 Nothing。</summary>
        Function FindTableFile(db As String, table As String) As String

        ' ==================== 会话访问 ====================

        ''' <summary>打开（或复用）一张表的表级会话。</summary>
        Function OpenSession(db As String, table As String) As ITableSession

        ''' <summary>已打开表的会话，未打开时返回 Nothing。</summary>
        Function TryGetSession(db As String, table As String) As ITableSession

        ' ==================== 加载 / 保存 ====================

        Function LoadTable(db As String, table As String) As StoredTable

        ''' <summary>
        ''' 只读取表结构，不打开表会话（因此不会获取表级文件锁）。用于 DESCRIBE /
        ''' SHOW COLUMNS 这类只需要列定义的语句，避免在多进程环境下无谓地等待锁。
        ''' 表不存在时抛出 <see cref="ArgumentException"/>。
        ''' </summary>
        Function LoadSchema(db As String, table As String) As TableSchema

        Sub SaveTable(db As String, table As StoredTable)
        Sub DeleteTable(db As String, table As String)

        ' ==================== 诊断 ====================

        ''' <summary>
        ''' 一个数据库内每张表的存储状态，用于 SHOW STORAGE。
        ''' 列：Table, Layout, Rows, Pending, WalBytes, DataBytes, File
        ''' </summary>
        Function DescribeStorage(db As String) As List(Of Object())

    End Interface
End Namespace
