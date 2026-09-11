Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Storage

    ''' <summary>
    ''' reads and writes the standalone table schema file &lt;table&gt;.schema.json.
    ''' the schema is written atomically (temp file + replace) and is completely
    ''' independent from the jsonl data file and its write ahead log.
    ''' </summary>
    Public Class SchemaStore

        Private Shared ReadOnly s_options As New JsonSerializerOptions With {
            .WriteIndented = True
        }

        Public Shared Function ToDto(schema As TableSchema) As JsonTableFile
            Dim file As New JsonTableFile With {
                .table = schema.TableName,
                .comment = schema.Comment
            }

            For Each key In schema.Keys
                file.keys.Add(New JsonKey With {
                    .name = key.Name,
                    .columns = key.Columns,
                    .unique = key.Unique,
                    .primary = key.Primary
                })
            Next

            For Each col In schema.Columns
                file.columns.Add(New JsonColumn With {
                    .name = col.Name,
                    .type = col.RawType,
                    .notNull = col.NotNull,
                    .primaryKey = col.PrimaryKey,
                    .defaultValue = col.DefaultValue,
                    .comment = col.Comment
                })
            Next

            Return file
        End Function

        Public Shared Function FromDto(file As JsonTableFile) As TableSchema
            Dim schema As New TableSchema With {
                .TableName = file.table,
                .Comment = file.comment
            }

            If file.keys IsNot Nothing Then
                For Each key In file.keys
                    schema.Keys.Add(New TableKeyInfo With {
                        .Name = key.name,
                        .Unique = key.unique,
                        .Primary = key.primary,
                        .Columns = If(key.columns, New List(Of String)())
                    })
                Next
            End If

            If file.columns IsNot Nothing Then
                For Each col In file.columns
                    schema.Columns.Add(New ColumnDef With {
                        .Name = col.name,
                        .TypeName = SqlTypes.NormalizeType(col.type),
                        .RawType = col.type,
                        .NotNull = col.NotNull,
                        .PrimaryKey = col.PrimaryKey,
                        .DefaultValue = RowJson.ConvertElement(ToElement(col.defaultValue)),
                        .Comment = col.comment
                    })
                Next
            End If

            Return schema
        End Function

        ''' <summary>
        ''' system.text.json gives back boxed <see cref="JsonElement"/> values when a
        ''' default value was read from the schema file
        ''' </summary>
        Private Shared Function ToElement(value As Object) As JsonElement
            If TypeOf value Is JsonElement Then
                Return DirectCast(value, JsonElement)
            End If

            Return Nothing
        End Function

        Public Shared Function Read(schemaPath As String) As TableSchema
            Dim json As String = System.IO.File.ReadAllText(schemaPath)
            Dim file As JsonTableFile = JsonSerializer.Deserialize(Of JsonTableFile)(json, s_options)

            If file Is Nothing Then
                Throw New InvalidDataException("invalid table schema file: " & schemaPath)
            End If

            Return FromDto(file)
        End Function

        ''' <summary>serialize the schema, used to detect whether a rewrite is needed</summary>
        Public Shared Function Serialize(schema As TableSchema) As String
            Return JsonSerializer.Serialize(ToDto(schema), s_options)
        End Function

        Public Shared Sub Write(schemaPath As String, schema As TableSchema)
            Dim json As String = Serialize(schema)
            Dim tmpPath As String = schemaPath & ".tmp"

            System.IO.File.WriteAllText(tmpPath, json, New UTF8Encoding(False))

            If System.IO.File.Exists(schemaPath) Then
                System.IO.File.Delete(schemaPath)
            End If

            System.IO.File.Move(tmpPath, schemaPath)
        End Sub

        ''' <summary>read the legacy single file table via the json table store</summary>
        Public Shared Function ReadLegacy(legacyPath As String) As StoredTable
            Return New JsonTableStore().Read(legacyPath)
        End Function
    End Class
End Namespace
