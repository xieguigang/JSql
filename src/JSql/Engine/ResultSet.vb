Namespace Engine

    ''' <summary>
    ''' execution result: either a tabular result set or a row-count message
    ''' </summary>
    Public Class ResultSet

        Public Property Columns As New List(Of String)
        Public Property Rows As New List(Of Object())
        Public Property RowsAffected As Integer
        Public Property Message As String

        Public ReadOnly Property IsQuery As Boolean
            Get
                Return Columns.Count > 0
            End Get
        End Property

        Public Shared Function FromQuery(columns As IEnumerable(Of String), rows As IEnumerable(Of Object())) As ResultSet
            Return New ResultSet With {
                .Columns = columns.ToList,
                .Rows = rows.ToList
            }
        End Function

        Public Shared Function FromMessage(message As String, Optional rowsAffected As Integer = 0) As ResultSet
            Return New ResultSet With {
                .Message = message,
                .RowsAffected = rowsAffected
            }
        End Function
    End Class
End Namespace
