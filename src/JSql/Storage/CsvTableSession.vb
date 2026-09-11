Namespace Storage

    ''' <summary>
    ''' CSV 表会话：数据文件为「表头行 + 数据行」，是 <see cref="TextTableSession"/>
    ''' 在 CSV 编解码器上的薄封装。列类型/NOT NULL/键/注释等元数据仍存放于
    ''' <c>&lt;表&gt;.schema.json</c>，CSV 文件本身只承载表头与行数据。
    ''' </summary>
    Public NotInheritable Class CsvTableSession
        Inherits TextTableSession

        Sub New(schema As TableSchema, schemaPath As String, dataPath As String, options As StorageOptions)
            MyBase.New(schema, schemaPath, dataPath, options, New CsvRowCodec())
        End Sub

    End Class
End Namespace
