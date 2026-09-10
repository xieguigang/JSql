Imports System.Collections.Generic
Imports JSql.Storage

Namespace Sql

    ''' <summary>binary operators in expressions</summary>
    Public Enum BinaryOp
        [Or]
        [And]
        Eq
        Ne
        Lt
        Le
        Gt
        Ge
        Add
        [Subtract]
        [Multiply]
        [Divide]
        [Mod]
    End Enum

    Public MustInherit Class SqlStatement
    End Class

    Public Class SelectItem

        Public Property Expression As Expression
        ''' <summary>output column alias (AS x or trailing x)</summary>
        Public Property [Alias] As String
    End Class

    Public Class JoinClause

        ''' <summary>"INNER" or "LEFT"</summary>
        Public Property JoinType As String
        Public Property Table As String
        Public Property [Alias] As String
        Public Property [On] As Expression
    End Class

    Public Class OrderItem

        Public Property Expression As Expression
        Public Property Descending As Boolean
    End Class

    Public Class SelectStatement : Inherits SqlStatement

        Public Property Distinct As Boolean
        Public Property SelectItems As New List(Of SelectItem)
        ''' <summary>nothing means a FROM-less select like SELECT 1+1</summary>
        Public Property FromTable As String
        Public Property FromAlias As String
        Public Property Joins As New List(Of JoinClause)
        Public Property Where As Expression
        Public Property GroupBy As New List(Of Expression)
        Public Property Having As Expression
        Public Property OrderBy As New List(Of OrderItem)
        ''' <summary>LIMIT n; -1 means no limit</summary>
        Public Property Limit As Integer = -1
        Public Property Offset As Integer = 0
    End Class

    Public Class Assignment

        Public Property Column As String
        Public Property Value As Expression
    End Class

    Public Class InsertStatement : Inherits SqlStatement

        Public Property Table As String
        ''' <summary>nothing means all schema columns in declared order</summary>
        Public Property Columns As List(Of String)
        Public Property ValueRows As New List(Of List(Of Expression))
    End Class

    Public Class UpdateStatement : Inherits SqlStatement

        Public Property Table As String
        Public Property Assignments As New List(Of Assignment)
        Public Property Where As Expression
    End Class

    Public Class DeleteStatement : Inherits SqlStatement

        Public Property Table As String
        Public Property Where As Expression
    End Class

    ' CREATE/DROP/USE/SHOW AND EXPRESSION APPEND MARKER
End Namespace
