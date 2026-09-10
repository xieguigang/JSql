Imports System.Collections.Generic
Imports JSql.Storage
Imports LINQ

Namespace Indexing

    ''' <summary>
    ''' adapts the json table rows onto the LINQ in-memory search index engine,
    ''' this type is the json counterpart of the LINQ MemoryTable.
    ''' </summary>
    Public Class JsonMemoryIndex : Inherits MemoryIndex

        Private ReadOnly rows As List(Of Dictionary(Of String, Object))

        Sub New(rows As List(Of Dictionary(Of String, Object)))
            Me.rows = rows
        End Sub

        ''' <summary>
        ''' every json cell is a scalar value, so the data field is always scalar here.
        ''' </summary>
        Protected Overrides Function CheckScalar(field As String) As Boolean
            Return True
        End Function

        Protected Overrides Function GetData(Of T)(field As String) As T()
            Select Case GetType(T)
                Case GetType(String)
                    Return DirectCast(CObj(rows.Select(Function(r) ToText(r, field)).ToArray()), T())
                Case GetType(Integer)
                    Return DirectCast(CObj(rows.Select(Function(r) ToInteger(r, field)).ToArray()), T())
                Case GetType(Double)
                    Return DirectCast(CObj(rows.Select(Function(r) ToNumber(r, field)).ToArray()), T())
                Case GetType(Date)
                    Return DirectCast(CObj(rows.Select(Function(r) ToDate(r, field)).ToArray()), T())
                Case Else
                    Throw New NotImplementedException(GetType(T).FullName)
            End Select
        End Function

        Public Sub BuildHash(field As String)
            Call HashIndex(field)
        End Sub

        Public Sub BuildRange(field As String, valueType As Type)
            Call ValueRange(field, valueType)
        End Sub

        Public Sub BuildFullText(field As String)
            Call FullText(field)
        End Sub

        ''' <summary>
        ''' run the LINQ search-index based row filter, returns zero based row offsets.
        ''' </summary>
        Public Function SelectRowOffsets(filter As IEnumerable(Of Query)) As Integer()
            Return GetIndex(filter)
        End Function

        Friend Function TermIndex(field As String) As TermHashIndex
            Return m_hashindex(field)
        End Function

        Friend Sub RestoreTermIndex(field As String, index As TermHashIndex)
            m_hashindex(field) = index
        End Sub

        Friend Function RangeValueIndex(field As String) As ValueIndex
            Return m_valueindex(field)
        End Function

        ''' <summary>
        ''' snapshot of one text column, used when persisting the index units.
        ''' </summary>
        Friend Function ColumnText(field As String) As String()
            Return rows.Select(Function(r) ToText(r, field)).ToArray()
        End Function

        Private Shared Function GetValue(row As Dictionary(Of String, Object), field As String) As Object
            Dim v As Object = Nothing

            If row IsNot Nothing AndAlso row.TryGetValue(field, v) Then
                Return v
            End If

            Return Nothing
        End Function

        Private Shared Function ToText(row As Dictionary(Of String, Object), field As String) As String
            Dim v As Object = GetValue(row, field)

            If v Is Nothing Then
                Return ""
            End If

            Return Convert.ToString(v)
        End Function

        ''' <summary>
        ''' NULL rows are indexed as zero: the index only works as a candidate
        ''' pre-filter here and every candidate row is re-checked by the sql engine
        ''' afterwards, so a NULL row never leaks into the final result set.
        ''' </summary>
        Private Shared Function ToInteger(row As Dictionary(Of String, Object), field As String) As Integer
            Dim v As Object = GetValue(row, field)

            If v Is Nothing Then
                Return 0
            End If

            Dim n As Double = 0

            If Double.TryParse(Convert.ToString(v), n) Then
                If n >= Integer.MinValue AndAlso n <= Integer.MaxValue Then
                    Return CInt(Math.Round(n))
                End If

                Return If(n > 0, Integer.MaxValue, Integer.MinValue)
            End If

            Return 0
        End Function

        Private Shared Function ToNumber(row As Dictionary(Of String, Object), field As String) As Double
            Dim v As Object = GetValue(row, field)

            If v Is Nothing Then
                Return 0
            End If

            Dim n As Double = 0

            If Double.TryParse(Convert.ToString(v), n) Then
                Return n
            End If

            Return 0
        End Function

        Private Shared Function ToDate(row As Dictionary(Of String, Object), field As String) As Date
            Dim v As Object = GetValue(row, field)

            If v Is Nothing Then
                Return New Date(1900, 1, 1)
            End If

            If TypeOf v Is Date Then
                Return CDate(v)
            End If

            Dim d As Date

            If Date.TryParse(Convert.ToString(v), d) Then
                Return d
            End If

            Return New Date(1900, 1, 1)
        End Function
    End Class
End Namespace
