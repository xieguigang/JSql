Imports Microsoft.VisualBasic.ComponentModel.DataSourceModel
Imports Microsoft.VisualBasic.Data.IO.ManagedSqlite.Core.SQLSchema
Imports Microsoft.VisualBasic.Data.IO.ManagedSqlite.Writer
Imports JSql.Storage

''' <summary>
''' JSql 表结构 / 行值与 SQLite 之间的映射：
''' <list type="bullet">
''' <item>JSql 规范类型 &lt;-&gt; SQLite 声明类型；</item>
''' <item><see cref="TableSchema"/> &lt;-&gt; <see cref="Sqlite3Column"/>；</item>
''' <item>由 SQLite 的 CREATE TABLE DDL 反解出 <see cref="TableSchema"/>（用于读取外部 sqlite 文件）；</item>
''' <item>行对象 &lt;-&gt; 定位子有序的值数组，并归一为 JSql 规范类型的 CLR 值。</item>
''' </list>
''' </summary>
Public Module SqliteSchemaMapper

    ''' <summary>
    ''' JSql 规范类型映射为 SQLite 列声明类型。
    ''' <para>
    ''' DATE / DATETIME 刻意映射为 TEXT：JSql 行模型里它们本就是
    ''' "yyyy-MM-dd[ HH:mm:ss]" 字符串，声明为 TEXT 可保证原样往返而不被
    ''' 托管引擎的整数亲和性转换破坏。
    ''' </para>
    ''' </summary>
    Public Function ToSqliteType(canonicalType As String) As String
        Select Case If(canonicalType, "").Trim().ToUpperInvariant()
            Case "INT"
                Return "INTEGER"
            Case "DOUBLE"
                Return "FLOAT"
            Case "BOOLEAN"
                Return "BOOLEAN"
            Case "DATE", "DATETIME"
                Return "TEXT"
            Case Else
                Return "TEXT"
        End Select
    End Function

    ''' <summary>
    ''' SQLite 列声明类型反解为 JSql 规范类型。BLOB 及无法识别的类型统一按
    ''' VARCHAR 处理（JSql 没有二进制类型）。
    ''' </summary>
    Public Function ToCanonicalType(declaredType As String) As String
        Dim raw As String = If(declaredType, "").Trim()

        If raw.Length = 0 Then
            Return "VARCHAR"
        End If

        Try
            Return SqlTypes.NormalizeType(raw)
        Catch ex As ArgumentException
            ' BLOB / 自定义类型等：按字符串承载
            Return "VARCHAR"
        End Try
    End Function

    ''' <summary>把一张表的 schema 列序映射为 SQLite 列定义（保持 JSql 的列顺序）。</summary>
    Public Function ToSqliteColumns(schema As TableSchema) As List(Of Sqlite3Column)
        Dim columns As New List(Of Sqlite3Column)

        For Each col As ColumnDef In schema.Columns
            columns.Add(New Sqlite3Column(col.Name,
                                          ToSqliteType(col.TypeName),
                                          col.NotNull,
                                          col.PrimaryKey))
        Next

        Return columns
    End Function

    ''' <summary>
    ''' 由 SQLite 的 CREATE TABLE 解析结果反解出 JSql 表结构。DDL 无法承载
    ''' COMMENT / DEFAULT / 表级 UNIQUE KEY 等元数据，这些字段留空。
    ''' </summary>
    Public Function FromSqliteSchema(sqlSchema As Schema, tableName As String) As TableSchema
        Dim schema As New TableSchema With {.TableName = tableName}

        If sqlSchema Is Nothing OrElse sqlSchema.columns Is Nothing Then
            Return schema
        End If

        Dim primaryKeys As String() = sqlSchema.PrimaryKeys

        For Each col As NamedValue(Of String) In sqlSchema.columns
            Dim isPrimaryKey As Boolean =
                primaryKeys IsNot Nothing AndAlso
                primaryKeys.Any(Function(p) String.Equals(p, col.Name, StringComparison.OrdinalIgnoreCase))

            schema.Columns.Add(New ColumnDef With {
                .Name = col.Name,
                .TypeName = ToCanonicalType(col.Value),
                .RawType = If(col.Value, "TEXT"),
                .NotNull = False,
                .PrimaryKey = isPrimaryKey
            })
        Next

        If primaryKeys IsNot Nothing AndAlso primaryKeys.Length > 0 Then
            Dim key As New TableKeyInfo With {
                .Name = "PRIMARY",
                .Unique = True,
                .Primary = True
            }

            key.Columns.AddRange(primaryKeys)
            schema.Keys.Add(key)
        End If

        Return schema
    End Function

    ''' <summary>
    ''' 把一行 JSql 行对象按 schema 列序编码成定位子有序的值数组，并归一为
    ''' SQLite 引擎可识别的 CLR 类型（Long / Double / String / Boolean / Nothing）。
    ''' </summary>
    Public Function ToValues(row As Dictionary(Of String, Object), schema As TableSchema) As Object()
        Dim values(schema.Columns.Count - 1) As Object

        For i As Integer = 0 To schema.Columns.Count - 1
            Dim col As ColumnDef = schema.Columns(i)
            Dim value As Object = Nothing

            If row IsNot Nothing Then
                row.TryGetValue(col.Name, value)
            End If

            values(i) = SqlTypes.CoerceValue(value, col.TypeName)
        Next

        Return values
    End Function

    ''' <summary>
    ''' 把 SQLite 引擎读回的值数组按 schema 列序还原为 JSql 行对象，并归一为
    ''' JSql 规范类型的 CLR 值。
    ''' </summary>
    Public Function FromValues(values As Object(), schema As TableSchema) As Dictionary(Of String, Object)
        Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)

        For i As Integer = 0 To schema.Columns.Count - 1
            Dim col As ColumnDef = schema.Columns(i)
            Dim value As Object = Nothing

            If values IsNot Nothing AndAlso i < values.Length Then
                value = values(i)
            End If

            row(col.Name) = SqlTypes.CoerceValue(value, col.TypeName)
        Next

        Return row
    End Function
End Module
