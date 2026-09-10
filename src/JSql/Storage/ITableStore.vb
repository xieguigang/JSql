Imports System.Collections.Generic
Imports System.IO

Namespace Storage

    ''' <summary>
    ''' sql canonical data types: INT, DOUBLE, VARCHAR, DATE, DATETIME, BOOLEAN
    ''' </summary>
    Public Module SqlTypes

        ''' <summary>
        ''' normalize a mysql type name into one of the canonical type names.
        ''' </summary>
        Public Function NormalizeType(typeName As String) As String
            Dim raw As String = If(typeName, "").Trim
            ' strip the type arguments: VARCHAR(50) -> VARCHAR
            Dim p As Integer = raw.IndexOf("("c)

            If p > 0 Then
                raw = raw.Substring(0, p).Trim()
            End If

            ' drop the trailing type attributes: "int unsigned" -> "int"
            Dim parts As String() = raw.Split(New Char() {" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)

            If parts.Length > 1 Then
                raw = parts(0)
            End If

            Dim t As String = raw.ToLower

            Select Case t
                Case "int", "integer", "bigint", "smallint", "tinyint", "mediumint"
                    Return "INT"
                Case "double", "float", "decimal", "numeric", "real"
                    Return "DOUBLE"
                Case "varchar", "char", "text", "longtext", "mediumtext", "tinytext", "string"
                    Return "VARCHAR"
                Case "date"
                    Return "DATE"
                Case "datetime", "timestamp"
                    Return "DATETIME"
                Case "boolean", "bool", "bit"
                    Return "BOOLEAN"
                Case Else
                    Throw New ArgumentException($"unsupported column data type: '{typeName}'")
            End Select
        End Function

        Public Function IsNumericType(canonicalType As String) As Boolean
            Return canonicalType = "INT" OrElse canonicalType = "DOUBLE"
        End Function

        ''' <summary>
        ''' coerce a raw runtime value into the target canonical sql type.
        ''' </summary>
        Public Function CoerceValue(value As Object, canonicalType As String) As Object
            If value Is Nothing Then
                Return Nothing
            End If

            Select Case canonicalType
                Case "INT"
                    If TypeOf value Is Boolean Then
                        Return If(CBool(value), 1L, 0L)
                    ElseIf TypeOf value Is String Then
                        Dim s As String = CStr(value).Trim
                        If s.Length = 0 Then Return Nothing
                        Return CLng(Math.Round(CDbl(s)))
                    Else
                        Return CLng(Math.Round(CDbl(Convert.ToDouble(value))))
                    End If
                Case "DOUBLE"
                    If TypeOf value Is Boolean Then
                        Return If(CBool(value), 1.0, 0.0)
                    ElseIf TypeOf value Is String Then
                        Dim s As String = CStr(value).Trim
                        If s.Length = 0 Then Return Nothing
                        Return CDbl(s)
                    Else
                        Return Convert.ToDouble(value)
                    End If
                Case "VARCHAR"
                    Return Convert.ToString(value)
                Case "DATE", "DATETIME"
                    If TypeOf value Is Date Then
                        Return FormatDate(CDate(value), canonicalType)
                    Else
                        Dim s As String = Convert.ToString(value).Trim
                        If s.Length = 0 Then Return Nothing

                        ' mysql: DEFAULT CURRENT_TIMESTAMP resolves at insert time
                        If s.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) OrElse
                           s.Equals("NOW()", StringComparison.OrdinalIgnoreCase) Then
                            Return FormatDate(DateTime.Now, canonicalType)
                        End If

                        Return FormatDate(CDate(s), canonicalType)
                    End If
                Case "BOOLEAN"
                    If TypeOf value Is String Then
                        Dim s As String = CStr(value).Trim.ToLower
                        If s = "true" OrElse s = "1" Then Return True
                        If s = "false" OrElse s = "0" Then Return False
                        Return Convert.ToBoolean(s)
                    ElseIf TypeOf value Is Boolean Then
                        Return CBool(value)
                    Else
                        Return Convert.ToDouble(value) <> 0
                    End If
                Case Else
                    Return value
            End Select
        End Function

        Public Function FormatDate(d As Date, canonicalType As String) As String
            If canonicalType = "DATE" Then
                Return d.ToString("yyyy-MM-dd")
            Else
                Return d.ToString("yyyy-MM-dd HH:mm:ss")
            End If
        End Function

        Public Function IsNumericValue(v As Object) As Boolean
            If v Is Nothing Then Return False
            Return TypeOf v Is SByte OrElse TypeOf v Is Byte OrElse TypeOf v Is Short OrElse
                   TypeOf v Is UShort OrElse TypeOf v Is Integer OrElse TypeOf v Is UInteger OrElse
                   TypeOf v Is Long OrElse TypeOf v Is ULong OrElse TypeOf v Is Single OrElse
                   TypeOf v Is Double OrElse TypeOf v Is Decimal
        End Function

        ''' <summary>
        ''' compare two non-null scalar values. numeric values are promoted and compared
        ''' numerically, everything else falls back to ordinal string comparison
        ''' (which also sorts iso-format date strings correctly).
        ''' </summary>
        Public Function CompareValues(a As Object, b As Object) As Integer
            If IsNumericValue(a) AndAlso IsNumericValue(b) Then
                Dim da As Double = Convert.ToDouble(a)
                Dim db As Double = Convert.ToDouble(b)
                Return da.CompareTo(db)
            End If
            Return String.Compare(Convert.ToString(a), Convert.ToString(b), StringComparison.Ordinal)
        End Function
    End Module

    ''' <summary>
    ''' a table level key definition: PRIMARY KEY / UNIQUE KEY / KEY as declared
    ''' inside the CREATE TABLE statement. this is metadata only, the physical
    ''' search index files are still managed through the CREATE INDEX statement.
    ''' </summary>
    Public Class TableKeyInfo

        Public Property Name As String
        Public Property Columns As New List(Of String)
        Public Property Unique As Boolean
        Public Property Primary As Boolean

        Public ReadOnly Property KeyText As String
            Get
                Return If(Primary, "PRIMARY", If(Unique, "UNIQUE", "KEY"))
            End Get
        End Property
    End Class

    Public Class ColumnDef
        Public Property Name As String
        ''' <summary>canonical type name, one of INT/DOUBLE/VARCHAR/DATE/DATETIME/BOOLEAN</summary>
        Public Property TypeName As String
        ''' <summary>original type text as declared in the create table statement</summary>
        Public Property RawType As String
        Public Property NotNull As Boolean
        Public Property PrimaryKey As Boolean
        Public Property DefaultValue As Object
        ''' <summary>the mysql column comment: COMMENT 'text'</summary>
        Public Property Comment As String
    End Class

    Public Class TableSchema
        Public Property TableName As String
        Public Property Columns As New List(Of ColumnDef)
        ''' <summary>the mysql table comment: COMMENT='text'</summary>
        Public Property Comment As String
        ''' <summary>table level key definitions declared inside CREATE TABLE</summary>
        Public Property Keys As New List(Of TableKeyInfo)

        Public Function FindColumn(name As String) As ColumnDef
            Return Columns.Where(Function(c) String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault
        End Function

        Public Function HasColumn(name As String) As Boolean
            Return FindColumn(name) IsNot Nothing
        End Function

        Public Function Clone() As TableSchema
            Dim copy As New TableSchema With {
                .TableName = TableName,
                .Comment = Comment
            }

            For Each col In Columns
                copy.Columns.Add(New ColumnDef With {
                    .Name = col.Name,
                    .TypeName = col.TypeName,
                    .RawType = col.RawType,
                    .NotNull = col.NotNull,
                    .PrimaryKey = col.PrimaryKey,
                    .DefaultValue = col.DefaultValue,
                    .Comment = col.Comment
                })
            Next

            For Each key In Keys
                Dim keyCopy As New TableKeyInfo With {
                    .Name = key.Name,
                    .Unique = key.Unique,
                    .Primary = key.Primary
                }

                keyCopy.Columns.AddRange(key.Columns)
                copy.Keys.Add(keyCopy)
            Next

            Return copy
        End Function
    End Class

    ''' <summary>
    ''' an in-memory representation of one physical table file: schema plus row data.
    ''' each row is a column-name -> scalar-value map.
    ''' </summary>
    Public Class StoredTable
        Public Property Schema As New TableSchema
        Public Property Rows As New List(Of Dictionary(Of String, Object))

        Public Function NewRow() As Dictionary(Of String, Object)
            Return New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        End Function
    End Class

    ''' <summary>
    ''' storage format abstraction: reads/writes a single table file.
    ''' the default json implementation treats one folder as a database and each
    ''' file inside as a table; new formats (e.g. csv) can be plugged in later.
    ''' </summary>
    Public Interface ITableStore
        ReadOnly Property FormatName As String
        ReadOnly Property FileExtension As String
        Function Read(filePath As String) As StoredTable
        Sub Write(filePath As String, table As StoredTable)
    End Interface

    ''' <summary>
    ''' dispatch table file io by file extension.
    ''' </summary>
    Public Class StorageFactory

        Private Shared ReadOnly s_stores As New Dictionary(Of String, ITableStore)(StringComparer.OrdinalIgnoreCase) From {
            {".json", New JsonTableStore}
        }

        Public Shared Function GetSupportedExtensions() As IEnumerable(Of String)
            Return s_stores.Keys.ToArray
        End Function

        ''' <summary>
        ''' pick the store implementation by file extension, json as the fallback default.
        ''' </summary>
        Public Shared Function GetStore(filePath As String) As ITableStore
            Dim ext As String = Path.GetExtension(filePath)

            If String.IsNullOrEmpty(ext) Then
                ext = ".json"
            End If

            Dim store As ITableStore = Nothing

            If s_stores.TryGetValue(ext, store) Then
                Return store
            End If

            Return s_stores(".json")
        End Function

        Public Shared Function GetDefaultStore() As ITableStore
            Return s_stores(".json")
        End Function
    End Class
End Namespace
