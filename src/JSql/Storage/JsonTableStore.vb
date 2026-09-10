Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Storage

    ''' <summary>
    ''' json table storage: one physical *.json file contains the table schema
    ''' plus all row data. writes are atomic (temp file + replace) to avoid
    ''' corrupting the table file on crash.
    ''' </summary>
    Public Class JsonTableStore : Implements ITableStore

        Private Shared ReadOnly s_jsonOptions As New JsonSerializerOptions With {
            .WriteIndented = True
        }

        Public ReadOnly Property FormatName As String Implements ITableStore.FormatName
            Get
                Return "JSON"
            End Get
        End Property

        Public ReadOnly Property FileExtension As String Implements ITableStore.FileExtension
            Get
                Return ".json"
            End Get
        End Property

        Public Function Read(filePath As String) As StoredTable Implements ITableStore.Read
            Dim json As String = System.IO.File.ReadAllText(filePath)
            Dim tableFile As JsonTableFile = JsonSerializer.Deserialize(Of JsonTableFile)(json, s_jsonOptions)

            If tableFile Is Nothing OrElse tableFile.columns Is Nothing Then
                Throw New InvalidDataException($"invalid json table file: {filePath}")
            End If

            Dim table As New StoredTable With {
                .Schema = New TableSchema With {
                    .TableName = tableFile.table,
                    .Comment = tableFile.comment
                }
            }

            If tableFile.keys IsNot Nothing Then
                For Each key In tableFile.keys
                    table.Schema.Keys.Add(New TableKeyInfo With {
                        .Name = key.name,
                        .Unique = key.unique,
                        .Primary = key.primary,
                        .Columns = If(key.columns, New List(Of String)())
                    })
                Next
            End If

            For Each col In tableFile.columns
                table.Schema.Columns.Add(New ColumnDef With {
                    .Name = col.name,
                    .TypeName = SqlTypes.NormalizeType(col.type),
                    .RawType = col.type,
                    .NotNull = col.NotNull,
                    .PrimaryKey = col.PrimaryKey,
                    .DefaultValue = JsonTableStore.ConvertJsonElement(col.defaultValue),
                    .Comment = col.comment
                })
            Next

            If tableFile.rows IsNot Nothing Then
                For Each row In tableFile.rows
                    Dim r As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)

                    If row IsNot Nothing Then
                        For Each kv In row
                            r(kv.Key) = JsonTableStore.ConvertJsonElement(kv.Value)
                        Next
                    End If

                    table.Rows.Add(r)
                Next
            End If

            Return table
        End Function

        Public Sub Write(filePath As String, table As StoredTable) Implements ITableStore.Write
            Dim tableFile As New JsonTableFile With {
                .table = table.Schema.TableName,
                .comment = table.Schema.Comment
            }

            For Each key In table.Schema.Keys
                tableFile.keys.Add(New JsonKey With {
                    .name = key.Name,
                    .columns = key.Columns,
                    .unique = key.Unique,
                    .primary = key.Primary
                })
            Next

            For Each col In table.Schema.Columns
                tableFile.columns.Add(New JsonColumn With {
                    .name = col.Name,
                    .type = col.RawType,
                    .notNull = col.NotNull,
                    .primaryKey = col.PrimaryKey,
                    .defaultValue = col.DefaultValue,
                    .comment = col.Comment
                })
            Next

            tableFile.rows = table.Rows

            Dim json As String = JsonSerializer.Serialize(tableFile, s_jsonOptions)
            Dim tmpPath As String = filePath & ".tmp"

            System.IO.File.WriteAllText(tmpPath, json, New UTF8Encoding(False))

            If System.IO.File.Exists(filePath) Then
                System.IO.File.Delete(filePath)
            End If

            System.IO.File.Move(tmpPath, filePath)
        End Sub

        ''' <summary>
        ''' system.text.json deserializes object values as boxed <see cref="JsonElement"/>,
        ''' unpack them into plain clr scalars here.
        ''' </summary>
        Friend Shared Function ConvertJsonElement(v As Object) As Object
            If v Is Nothing Then
                Return Nothing
            End If

            If Not TypeOf v Is JsonElement Then
                Return v
            End If

            Dim je As JsonElement = DirectCast(v, JsonElement)

            Select Case je.ValueKind
                Case JsonValueKind.String : Return je.GetString()
                Case JsonValueKind.Number
                    Dim int64Value As Long = 0
                    If je.TryGetInt64(int64Value) Then
                        Return int64Value
                    Else
                        Return je.GetDouble()
                    End If
                Case JsonValueKind.True : Return True
                Case JsonValueKind.False : Return False
                Case Else : Return Nothing
            End Select
        End Function
    End Class

    ' dto models used for json (de)serialization

    Public Class JsonTableFile
        Public Property table As String
        ''' <summary>the mysql table comment: COMMENT='text'</summary>
        Public Property comment As String
        ''' <summary>table level key definitions declared inside CREATE TABLE</summary>
        Public Property keys As New List(Of JsonKey)
        Public Property columns As New List(Of JsonColumn)
        Public Property rows As List(Of Dictionary(Of String, Object))
    End Class

    Public Class JsonColumn
        Public Property name As String
        Public Property type As String
        Public Property notNull As Boolean
        Public Property primaryKey As Boolean
        Public Property defaultValue As Object
        ''' <summary>the mysql column comment: COMMENT 'text'</summary>
        Public Property comment As String
    End Class

    Public Class JsonKey
        Public Property name As String
        Public Property columns As New List(Of String)
        Public Property unique As Boolean
        Public Property primary As Boolean
    End Class
End Namespace
