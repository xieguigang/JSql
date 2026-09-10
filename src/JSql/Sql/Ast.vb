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

    Public Enum CreateKind
        [Table]
        [Index]
        [Database]
    End Enum

    Public Class CreateStatement : Inherits SqlStatement

        Public Property Kind As CreateKind
        Public Property IfNotExists As Boolean
        ''' <summary>table name / database name / index name</summary>
        Public Property Name As String
        ''' <summary>for CREATE TABLE: column definitions</summary>
        Public Property Columns As New List(Of ColumnDef)
        ''' <summary>for CREATE INDEX: the target table</summary>
        Public Property OnTable As String
        ''' <summary>for CREATE INDEX: the target column</summary>
        Public Property OnColumn As String
        ''' <summary>for CREATE INDEX: HASH / RANGE(BTREE) / FULLTEXT, nothing = auto pick</summary>
        Public Property IndexKind As String
        ''' <summary>for CREATE TABLE: the table level COMMENT='text' option</summary>
        Public Property TableComment As String
        ''' <summary>for CREATE TABLE: the table level PRIMARY KEY/UNIQUE KEY/KEY definitions</summary>
        Public Property Keys As New List(Of TableKeyInfo)
    End Class

    Public Enum DropKind
        [Table]
        [Index]
        [Database]
    End Enum

    Public Class DropStatement : Inherits SqlStatement

        Public Property Kind As DropKind
        Public Property IfExists As Boolean
        Public Property Name As String
        ''' <summary>for DROP INDEX: the owner table</summary>
        Public Property OnTable As String
    End Class

    Public Class UseStatement : Inherits SqlStatement

        Public Property Database As String
    End Class

    Public Enum ShowKind
        Databases
        Tables
        Indexes
        Columns
    End Enum

    Public Class ShowStatement : Inherits SqlStatement

        Public Property Kind As ShowKind
        ''' <summary>Tables: database name (nothing = current); Indexes/Columns: table name</summary>
        Public Property Target As String
    End Class

    ' ==================== expressions ====================

    Public MustInherit Class Expression
    End Class

    Public Class LiteralExpression : Inherits Expression

        Public Property Value As Object
        Public Property IsNull As Boolean

        Sub New()
        End Sub

        Sub New(value As Object)
            Me.Value = value
        End Sub

        Public Shared Function NullLiteral() As LiteralExpression
            Return New LiteralExpression With {.IsNull = True}
        End Function
    End Class

    Public Class IdentifierExpression : Inherits Expression

        ''' <summary>table name/alias qualifier, nothing for a bare column reference</summary>
        Public Property Qualifier As String
        Public Property Name As String

        Sub New()
        End Sub

        Sub New(name As String)
            Me.Name = name
        End Sub

        Sub New(qualifier As String, name As String)
            Me.Qualifier = qualifier
            Me.Name = name
        End Sub
    End Class

    Public Class StarExpression : Inherits Expression

        Public Property Qualifier As String
    End Class

    Public Class UnaryExpression : Inherits Expression

        ''' <summary>"-" or "NOT"</summary>
        Public Property Op As String
        Public Property Operand As Expression
    End Class

    Public Class BinaryExpression : Inherits Expression

        Public Property Op As BinaryOp
        Public Property Left As Expression
        Public Property Right As Expression
    End Class

    Public Class FunctionCallExpression : Inherits Expression

        ''' <summary>upper-cased function name: COUNT/SUM/AVG/MIN/MAX</summary>
        Public Property Name As String
        Public Property Args As New List(Of Expression)
        ''' <summary>true for COUNT(*)</summary>
        Public Property IsStar As Boolean
    End Class

    Public Class InExpression : Inherits Expression

        Public Property Operand As Expression
        Public Property Values As New List(Of Expression)
        Public Property Negated As Boolean
    End Class

    Public Class BetweenExpression : Inherits Expression

        Public Property Operand As Expression
        Public Property Low As Expression
        Public Property High As Expression
        Public Property Negated As Boolean
    End Class

    Public Class LikeExpression : Inherits Expression

        Public Property Operand As Expression
        Public Property Pattern As Expression
        Public Property Negated As Boolean
    End Class

    Public Class IsNullExpression : Inherits Expression

        Public Property Operand As Expression
        Public Property Negated As Boolean
    End Class
End Namespace
