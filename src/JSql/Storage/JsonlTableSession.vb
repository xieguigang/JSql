Namespace Storage

    ''' <summary>
    ''' JSONL 表会话：以「一行一个 JSON 对象」的方式存取，是 <see cref="TextTableSession"/>
    ''' 在 JSONL 编解码器上的薄封装，保持既有 JSONL 表的行为与文件布局不变。
    ''' </summary>
    Public NotInheritable Class JsonlTableSession
        Inherits TextTableSession

        Sub New(schema As TableSchema, schemaPath As String, dataPath As String, options As StorageOptions)
            MyBase.New(schema, schemaPath, dataPath, options, New JsonRowCodec())
        End Sub

    End Class
End Namespace
