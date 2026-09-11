Namespace Storage

    ''' <summary>
    ''' JSONL 行编解码：一行一个紧凑的 JSON 对象。实现委托给既有的 <see cref="RowJson"/>，
    ''' 以保证与历史数据文件字节级一致。
    ''' </summary>
    Public Class JsonRowCodec : Implements IRowCodec

        Public ReadOnly Property LayoutName As String Implements IRowCodec.LayoutName
            Get
                Return "JSONL"
            End Get
        End Property

        Public ReadOnly Property HasHeader As Boolean Implements IRowCodec.HasHeader
            Get
                Return False
            End Get
        End Property

        Public Sub UseHeader(headerLine As String, schema As TableSchema) Implements IRowCodec.UseHeader
            ' JSONL 没有表头行，不需要建立列序映射
        End Sub

        Public Function BuildHeader(schema As TableSchema) As String Implements IRowCodec.BuildHeader
            Return Nothing
        End Function

        Public Function SerializeLine(row As Dictionary(Of String, Object), schema As TableSchema) As String Implements IRowCodec.SerializeLine
            Return RowJson.Serialize(row, schema)
        End Function

        Public Function DeserializeLine(line As String, schema As TableSchema) As Dictionary(Of String, Object) Implements IRowCodec.DeserializeLine
            Return RowJson.Deserialize(line)
        End Function

    End Class
End Namespace
