Namespace Storage

    ''' <summary>
    ''' 行编解码抽象：把一行数据在“内存行对象”与“磁盘上的单行文本”之间互相转换。
    ''' 底层的 <see cref="Microsoft.VisualBasic.Data.Repository.TextLineStore"/> 只负责按行存放不透明文本，
    ''' 具体是 JSONL 还是 CSV 由本接口的实现决定，从而让会话层与存储格式解耦。
    ''' </summary>
    Public Interface IRowCodec

        ''' <summary>格式名称，用于 SHOW STORAGE 等展示，例如 "JSONL" / "CSV"。</summary>
        ReadOnly Property LayoutName As String

        ''' <summary>
        ''' 数据文件的物理首行是否为表头行。CSV=True（首行是列名），JSONL=False。
        ''' </summary>
        ReadOnly Property HasHeader As Boolean

        ''' <summary>
        ''' 用数据文件的物理表头行建立“列名 -> 物理序号”的映射，以支持外部 CSV 的列序与
        ''' schema 列序不一致的情况。无表头格式（如 JSONL）应忽略该调用。
        ''' </summary>
        ''' <param name="headerLine">数据文件的表头行文本；可能为空（空文件）。</param>
        ''' <param name="schema">目标表结构（列序的权威来源）。</param>
        Sub UseHeader(headerLine As String, schema As TableSchema)

        ''' <summary>构建表头行文本。无表头格式返回 Nothing。</summary>
        Function BuildHeader(schema As TableSchema) As String

        ''' <summary>把一个行对象编码为一行文本（不含换行符）。</summary>
        Function SerializeLine(row As Dictionary(Of String, Object), schema As TableSchema) As String

        ''' <summary>把一行文本解码为行对象。</summary>
        Function DeserializeLine(line As String, schema As TableSchema) As Dictionary(Of String, Object)

    End Interface
End Namespace
