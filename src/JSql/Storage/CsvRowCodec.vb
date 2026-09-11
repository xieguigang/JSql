Imports System.Globalization
Imports System.IO
Imports Microsoft.VisualBasic.Data.Framework.IO
Imports Microsoft.VisualBasic.Data.Framework.IO.CSVFile
Imports Microsoft.VisualBasic.Data.Framework.StorageProvider

Namespace Storage

    ''' <summary>
    ''' CSV 行编解码：数据文件的物理结构为「第 1 行表头 + 后续数据行」。
    ''' <list type="bullet">
    ''' <item>表头：用 <see cref="HeaderSchema"/> 解析列名并建立「列名 -&gt; 物理序号」映射，
    '''       以支持外部 CSV 的列序与 schema 列序不一致；生成行时按 schema 列序输出。</item>
    ''' <item>解析：用 <see cref="Tokenizer.CharsParser"/> 单遍字符扫描切分单元格，
    '''       再经 <see cref="SqlTypes.CoerceValue"/> 按列类型还原为强类型的值。</item>
    ''' <item>生成：用 <see cref="RowObject.ToString(IEnumerable(Of String), String)"/> 逐格转义并拼接。</item>
    ''' </list>
    ''' 由于底层是行式存储引擎（一行一条记录），单元格内的 CR/LF 在写盘前会被规范化为空格。
    ''' </summary>
    Public Class CsvRowCodec : Implements IRowCodec

        ''' <summary>字段分隔符。</summary>
        Public Const Delimiter As String = ","

        ''' <summary>数据文件表头的列名映射；Nothing = 按 schema 列序读取。</summary>
        Private _header As HeaderSchema

        Public ReadOnly Property LayoutName As String Implements IRowCodec.LayoutName
            Get
                Return "CSV"
            End Get
        End Property

        Public ReadOnly Property HasHeader As Boolean Implements IRowCodec.HasHeader
            Get
                Return True
            End Get
        End Property

        Public Sub UseHeader(headerLine As String, schema As TableSchema) Implements IRowCodec.UseHeader
            If schema Is Nothing OrElse String.IsNullOrWhiteSpace(headerLine) Then
                _header = Nothing
                Return
            End If

            Try
                Dim tokens As String() = Tokenizer.CharsParser(headerLine, CChar(Delimiter)).ToArray()
                _header = New HeaderSchema(tokens)
            Catch ex As Exception
                ' HeaderSchema 在表头重名时会抛出 DuplicateNameException，这里转换成可读错误
                Throw New InvalidDataException("CSV 表头解析失败：" & ex.Message, ex)
            End Try
        End Sub

        Public Function BuildHeader(schema As TableSchema) As String Implements IRowCodec.BuildHeader
            Return RowObject.ToString(schema.Columns.Select(Function(c) c.Name), Delimiter)
        End Function

        Public Function SerializeLine(row As Dictionary(Of String, Object), schema As TableSchema) As String Implements IRowCodec.SerializeLine
            Dim cells As New List(Of String)(schema.Columns.Count)

            For Each col In schema.Columns
                Dim value As Object = Nothing

                If row IsNot Nothing Then
                    row.TryGetValue(col.Name, value)
                End If

                cells.Add(FormatCell(value, col.TypeName))
            Next

            Return RowObject.ToString(cells, Delimiter)
        End Function

        Public Function DeserializeLine(line As String, schema As TableSchema) As Dictionary(Of String, Object) Implements IRowCodec.DeserializeLine
            Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)

            If schema Is Nothing Then
                Return row
            End If

            If String.IsNullOrWhiteSpace(line) Then
                For Each col In schema.Columns
                    row(col.Name) = Nothing
                Next

                Return row
            End If

            Dim tokens As String() = Tokenizer.CharsParser(line, CChar(Delimiter)).ToArray()

            For i As Integer = 0 To schema.Columns.Count - 1
                Dim col As ColumnDef = schema.Columns(i)
                Dim idx As Integer = i

                If _header IsNot Nothing Then
                    Dim ordinal As Integer = _header.GetOrdinal(col.Name)

                    If ordinal < 0 Then
                        ' 外部文件的表头里没有这一列
                        row(col.Name) = Nothing
                        Continue For
                    End If

                    idx = ordinal
                End If

                Dim raw As String = If(idx >= 0 AndAlso idx < tokens.Length, tokens(idx), Nothing)
                row(col.Name) = ParseCell(raw, col.TypeName)
            Next

            Return row
        End Function

        ''' <summary>把运行时值格式化为 CSV 单元格文本。NULL -&gt; 空串；单元格内 CR/LF 规范化为空格。</summary>
        Private Shared Function FormatCell(value As Object, typeName As String) As String
            If value Is Nothing Then
                Return ""
            End If

            If TypeOf value Is Boolean Then
                Return If(CBool(value), "1", "0")
            End If

            If TypeOf value Is Date Then
                Return SqlTypes.FormatDate(CDate(value), If(typeName = "DATE", "DATE", "DATETIME"))
            End If

            If TypeOf value Is String Then
                Return NormalizeNewLines(CStr(value))
            End If

            If SqlTypes.IsNumericValue(value) Then
                Dim d As Double = Convert.ToDouble(value)

                ' 整数保持整数形态，避免 CSV 中出现多余的小数点（与 JSONL 行一致）
                If Math.Truncate(d) = d AndAlso Math.Abs(d) <= 9.2E+18 Then
                    Return CLng(d).ToString(CultureInfo.InvariantCulture)
                End If

                Return d.ToString(CultureInfo.InvariantCulture)
            End If

            Return NormalizeNewLines(Convert.ToString(value))
        End Function

        ''' <summary>把单元格文本按列类型还原为强类型值。空串 -&gt; NULL。</summary>
        Private Shared Function ParseCell(raw As String, typeName As String) As Object
            If raw Is Nothing OrElse raw.Length = 0 Then
                Return Nothing
            End If

            Return SqlTypes.CoerceValue(raw, typeName)
        End Function

        ''' <summary>把单元格内的 CR/LF 替换为空格，维持行式引擎“一行一条记录”的不变量。</summary>
        Private Shared Function NormalizeNewLines(value As String) As String
            If String.IsNullOrEmpty(value) Then
                Return value
            End If

            Return value _
                .Replace(vbCrLf, " ") _
                .Replace(vbCr, " ") _
                .Replace(vbLf, " ")
        End Function

    End Class
End Namespace
