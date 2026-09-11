Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json

Namespace Storage

    ''' <summary>
    ''' jsonl row codec: one table row is stored as one compact json object line,
    ''' the columns are written in schema order so that the file stays diff friendly.
    ''' </summary>
    Public Class RowJson

        ''' <summary>serialize a row into a single line json object</summary>
        Public Shared Function Serialize(row As Dictionary(Of String, Object), schema As TableSchema) As String
            Using ms As New MemoryStream()
                Using writer As New Utf8JsonWriter(ms)
                    writer.WriteStartObject()

                    Dim written As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

                    If schema IsNot Nothing Then
                        For Each col In schema.Columns
                            Dim value As Object = Nothing

                            If row IsNot Nothing Then
                                row.TryGetValue(col.Name, value)
                            End If

                            WriteValue(writer, col.Name, value)
                            written.Add(col.Name)
                        Next
                    End If

                    If row IsNot Nothing Then
                        For Each kv In row
                            If Not written.Contains(kv.Key) Then
                                WriteValue(writer, kv.Key, kv.Value)
                            End If
                        Next
                    End If

                    writer.WriteEndObject()
                End Using

                Return Encoding.UTF8.GetString(ms.ToArray())
            End Using
        End Function

        Private Shared Sub WriteValue(writer As Utf8JsonWriter, name As String, value As Object)
            If value Is Nothing Then
                writer.WriteNull(name)
                Return
            End If

            If TypeOf value Is Boolean Then
                writer.WriteBoolean(name, CBool(value))
            ElseIf TypeOf value Is String Then
                writer.WriteString(name, CStr(value))
            ElseIf TypeOf value Is Date Then
                writer.WriteString(name, SqlTypes.FormatDate(CDate(value), "DATETIME"))
            ElseIf SqlTypes.IsNumericValue(value) Then
                Dim d As Double = Convert.ToDouble(value)

                ' keep the integer values as integers: a jsonl line is diffed as text
                If Math.Truncate(d) = d AndAlso Math.Abs(d) <= 9.2E+18 Then
                    writer.WriteNumber(name, CLng(d))
                Else
                    writer.WriteNumber(name, d)
                End If
            Else
                writer.WriteString(name, Convert.ToString(value))
            End If
        End Sub

        ''' <summary>parse one jsonl line back into a row</summary>
        Public Shared Function Deserialize(line As String) As Dictionary(Of String, Object)
            Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)

            If String.IsNullOrWhiteSpace(line) Then
                Return row
            End If

            Using doc As JsonDocument = JsonDocument.Parse(line)
                For Each prop As JsonProperty In doc.RootElement.EnumerateObject()
                    row(prop.Name) = ConvertElement(prop.Value)
                Next
            End Using

            Return row
        End Function

        ''' <summary>unpack a json element into a plain clr scalar</summary>
        Public Shared Function ConvertElement(element As JsonElement) As Object
            Select Case element.ValueKind
                Case JsonValueKind.String
                    Return element.GetString()
                Case JsonValueKind.Number
                    Dim asLong As Long = 0

                    If element.TryGetInt64(asLong) Then
                        Return asLong
                    Else
                        Return element.GetDouble()
                    End If
                Case JsonValueKind.True
                    Return True
                Case JsonValueKind.False
                    Return False
                Case Else
                    Return Nothing
            End Select
        End Function
    End Class
End Namespace
