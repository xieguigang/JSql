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
            Dim json As String = File.ReadAllText(filePath)
            Dim file As JsonTableFile = JsonSerializer.Deserialize(Of JsonTableFile)(json, s_jsonOptions)

            If file Is Nothing OrElse file.columns Is Nothing Then
                Throw New InvalidDataException($"invalid json table file: {filePath}")
            End If

            Dim table As New StoredTable With {
                .Schema = New TableSchema With {.TableName = file.table}
            }

            For Each col In file.columns
                table.Schema.Columns.Add(New ColumnDef With {
                    .Name = col.name,
                    .TypeName = SqlTypes.NormalizeType(col.type),
                    .RawType = col.type,
                    .NotNull = col.NotNull,
                    .PrimaryKey = col.PrimaryKey,
                    .DefaultValue = JsonTableStore.ConvertJsonElement(col.defaultValue)
                })
            Next

            If file.rows IsNot Nothing Then
                For Each row In file.rows
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
            Dim file As New JsonTableFile With {
                .table = table.Schema.TableName
            }

            For Each col In table.Schema.Columns
                file.columns.Add(New JsonColumn With {
                    .name = col.Name,
                    .type = col.RawType,
                    .notNull = col.NotNull,
                    .primaryKey = col.PrimaryKey,
                    .defaultValue = col.DefaultValue
                })
            Next

            file.rows = table.Rows

            Dim json As String = JsonSerializer.Serialize(file, s_jsonOptions)
            Dim tmpPath As String = filePath & ".tmp"

            File.WriteAllText(tmpPath, json, New UTF8Encoding(False))

            If File.Exists(filePath) Then
                File.Delete(filePath)
            End If

            File.Move(tmpPath, filePath)
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
                    If je.TryGetInt64() Then
                        Return je.GetInt64()
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
        Public Property columns As New List(Of JsonColumn)
        Public Property rows As List(Of Dictionary(Of String, Object))
    End Class

    Public Class JsonColumn
        Public Property name As String
        Public Property type As String
        Public Property notNull As Boolean
        Public Property primaryKey As Boolean
        Public Property defaultValue As Object
    End Class
End Namespace
