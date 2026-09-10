Imports System.Collections.Generic
Imports System.Text.RegularExpressions
Imports JSql.Sql
Imports JSql.Storage

Namespace Engine

    Public Class RowScope

        Public Property Tables As New Dictionary(Of String, Dictionary(Of String, Object))(StringComparer.OrdinalIgnoreCase)

        Public Sub Add(Optional aliasName As String = Nothing, row As Dictionary(Of String, Object) = Nothing)
            If aliasName Is Nothing Then
                aliasName = If(Tables.Count = 0, "@ROW", "@T" & Tables.Count)
            End If

            Tables(aliasName) = row
        End Sub

        Public Function Clone() As RowScope
            Dim copy As New RowScope

            For Each kv In Tables
                copy.Tables(kv.Key) = kv.Value
            Next

            Return copy
        End Function
    End Class

    Public Class ExpressionEvaluator

        Private Shared ReadOnly AGGREGATES As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "COUNT", "SUM", "AVG", "MIN", "MAX"
        }

        Private ReadOnly scope As RowScope
        Private ReadOnly groupRows As List(Of RowScope)

        Sub New(scope As RowScope, Optional groupRows As List(Of RowScope) = Nothing)
            Me.scope = scope
            Me.groupRows = groupRows
        End Sub

        Public Shared Function ContainsAggregate(expr As Expression) As Boolean
            If expr Is Nothing Then
                Return False
            End If

            If TypeOf expr Is FunctionCallExpression Then
                If AGGREGATES.Contains(DirectCast(expr, FunctionCallExpression).Name) Then
                    Return True
                End If
            End If

            If TypeOf expr Is BinaryExpression Then
                Dim b = DirectCast(expr, BinaryExpression)
                Return ContainsAggregate(b.Left) OrElse ContainsAggregate(b.Right)
            End If

            Return False
        End Function

        Public Function Eval(expr As Expression) As Object
            If TypeOf expr Is LiteralExpression Then
                Dim lit = DirectCast(expr, LiteralExpression)
                If lit.IsNull Then
                    Return Nothing
                End If
                Return lit.Value
            End If

            If TypeOf expr Is IdentifierExpression Then
                Return EvalColumn(DirectCast(expr, IdentifierExpression))
            End If

            If TypeOf expr Is StarExpression Then
                Throw New SqlError("the star operator can only be used inside COUNT(*)")
            End If

            If TypeOf expr Is UnaryExpression Then
                Dim u = DirectCast(expr, UnaryExpression)
                Dim v As Object = Eval(u.Operand)

                If u.Op = "NOT" Then
                    If v Is Nothing Then
                        Return Nothing
                    End If
                    Return Not ToBoolean(v)
                End If

                If v Is Nothing Then
                    Return Nothing
                End If

                Return -ToDouble(v)
            End If

            If TypeOf expr Is BinaryExpression Then
                Return EvalBinary(DirectCast(expr, BinaryExpression))
            End If

            If TypeOf expr Is FunctionCallExpression Then
                Return EvalFunction(DirectCast(expr, FunctionCallExpression))
            End If

            If TypeOf expr Is IsNullExpression Then
                Dim n = DirectCast(expr, IsNullExpression)
                Dim v As Object = Eval(n.Operand)

                If n.Negated Then
                    Return v IsNot Nothing
                End If

                Return v Is Nothing
            End If

            If TypeOf expr Is BetweenExpression Then
                Dim bt = DirectCast(expr, BetweenExpression)
                Dim v As Object = Eval(bt.Operand)
                Dim lo As Object = Eval(bt.Low)
                Dim hi As Object = Eval(bt.High)

                If v Is Nothing OrElse lo Is Nothing OrElse hi Is Nothing Then
                    Return Nothing
                End If

                Dim hit As Boolean = SqlTypes.CompareValues(v, lo) >= 0 AndAlso SqlTypes.CompareValues(v, hi) <= 0

                Return If(bt.Negated, Not hit, hit)
            End If

            If TypeOf expr Is InExpression Then
                Dim ine = DirectCast(expr, InExpression)
                Dim v As Object = Eval(ine.Operand)
                Dim hit As Boolean = False

                For Each candidate In ine.Values
                    Dim c As Object = Eval(candidate)

                    If v Is Nothing OrElse c Is Nothing Then
                        Continue For
                    End If

                    If SqlTypes.CompareValues(v, c) = 0 Then
                        hit = True
                        Exit For
                    End If
                Next

                Return If(ine.Negated, Not hit, hit)
            End If

            If TypeOf expr Is LikeExpression Then
                Dim lk = DirectCast(expr, LikeExpression)
                Dim v As Object = Eval(lk.Operand)
                Dim pattern As Object = Eval(lk.Pattern)

                If v Is Nothing OrElse pattern Is Nothing Then
                    Return Nothing
                End If

                Dim hit As Boolean = Regex.IsMatch(Convert.ToString(v), LikeToRegex(Convert.ToString(pattern)), RegexOptions.IgnoreCase)

                Return If(lk.Negated, Not hit, hit)
            End If

            Throw New SqlError("unsupported sql expression node: " & expr.GetType().Name)
        End Function

        Private Function EvalBinary(b As BinaryExpression) As Object
            Select Case b.Op
                Case BinaryOp.And
                    Dim l As Object = Eval(b.Left)

                    If l IsNot Nothing AndAlso Not ToBoolean(l) Then
                        Return False
                    End If

                    Dim r As Object = Eval(b.Right)

                    If l Is Nothing OrElse r Is Nothing Then
                        Return Nothing
                    End If

                    Return ToBoolean(r)

                Case BinaryOp.Or
                    Dim l As Object = Eval(b.Left)

                    If l IsNot Nothing AndAlso ToBoolean(l) Then
                        Return True
                    End If

                    Dim r As Object = Eval(b.Right)

                    If l Is Nothing OrElse r Is Nothing Then
                        Return Nothing
                    End If

                    Return ToBoolean(r)

                Case BinaryOp.Eq, BinaryOp.Ne, BinaryOp.Lt, BinaryOp.Le, BinaryOp.Gt, BinaryOp.Ge
                    Return EvalCompare(b)

                Case Else
                    Return EvalArithmetic(b)
            End Select
        End Function

        Private Function EvalCompare(b As BinaryExpression) As Object
            Dim l As Object = Eval(b.Left)
            Dim r As Object = Eval(b.Right)

            If l Is Nothing OrElse r Is Nothing Then
                Return Nothing
            End If

            Dim cmp As Integer = SqlTypes.CompareValues(l, r)

            Select Case b.Op
                Case BinaryOp.Eq : Return cmp = 0
                Case BinaryOp.Ne : Return cmp <> 0
                Case BinaryOp.Lt : Return cmp < 0
                Case BinaryOp.Le : Return cmp <= 0
                Case BinaryOp.Gt : Return cmp > 0
                Case BinaryOp.Ge : Return cmp >= 0
            End Select

            Return Nothing
        End Function

        Private Function EvalArithmetic(b As BinaryExpression) As Object
            Dim l As Object = Eval(b.Left)
            Dim r As Object = Eval(b.Right)

            If l Is Nothing OrElse r Is Nothing Then
                Return Nothing
            End If

            Dim bothInt As Boolean = TypeOf l Is Long AndAlso TypeOf r Is Long

            Select Case b.Op
                Case BinaryOp.Add
                    If Not (SqlTypes.IsNumericValue(l) AndAlso SqlTypes.IsNumericValue(r)) Then
                        Return Convert.ToString(l) & Convert.ToString(r)
                    End If

                    If bothInt Then
                        Return CLng(l) + CLng(r)
                    End If

                    Return ToDouble(l) + ToDouble(r)

                Case BinaryOp.Subtract
                    If bothInt Then
                        Return CLng(l) - CLng(r)
                    End If

                    Return ToDouble(l) - ToDouble(r)

                Case BinaryOp.Multiply
                    If bothInt Then
                        Return CLng(l) * CLng(r)
                    End If

                    Return ToDouble(l) * ToDouble(r)

                Case BinaryOp.Divide
                    Dim d As Double = ToDouble(r)

                    If d = 0 Then
                        Return Nothing
                    End If

                    Return ToDouble(l) / d

                Case BinaryOp.Mod
                    Dim m As Long = CLng(ToDouble(r))

                    If m = 0 Then
                        Return Nothing
                    End If

                    Return CLng(ToDouble(l)) Mod m
            End Select

            Return Nothing
        End Function

        Private Function EvalColumn(col As IdentifierExpression) As Object
            If col.Qualifier IsNot Nothing Then
                Dim t As Dictionary(Of String, Object) = Nothing

                If Not scope.Tables.TryGetValue(col.Qualifier, t) Then
                    Throw New SqlError("unknown table alias: " & col.Qualifier)
                End If

                If t Is Nothing Then
                    Return Nothing
                End If

                Dim v As Object = Nothing

                If Not t.TryGetValue(col.Name, v) Then
                    Throw New SqlError("unknown column: " & col.Qualifier & "." & col.Name)
                End If

                Return v
            End If

            Dim found As Object = Nothing
            Dim hits As Integer = 0

            For Each table In scope.Tables.Values
                If table IsNot Nothing AndAlso table.ContainsKey(col.Name) Then
                    hits += 1
                    found = table(col.Name)
                End If
            Next

            If hits > 1 Then
                Throw New SqlError("ambiguous column reference: " & col.Name)
            End If

            If hits = 0 Then
                Throw New SqlError("unknown column: " & col.Name)
            End If

            Return found
        End Function

        Private Function EvalFunction(fn As FunctionCallExpression) As Object
            If AGGREGATES.Contains(fn.Name) Then
                Return EvalAggregate(fn)
            End If

            Throw New SqlError("unsupported function: " & fn.Name)
        End Function

        Private Function EvalAggregate(fn As FunctionCallExpression) As Object
            If groupRows Is Nothing Then
                Throw New SqlError("invalid use of the aggregate function " & fn.Name)
            End If

            Dim name As String = fn.Name.ToUpper

            If fn.IsStar Then
                If name = "COUNT" Then
                    Return CLng(groupRows.Count)
                End If

                Throw New SqlError("COUNT(*) is the only star-form aggregate supported")
            End If

            If fn.Args.Count <> 1 Then
                Throw New SqlError(fn.Name & " requires exactly one argument")
            End If

            Dim values As New List(Of Object)

            For Each row In groupRows
                Dim v As Object = New ExpressionEvaluator(row).Eval(fn.Args(0))

                If v IsNot Nothing Then
                    values.Add(v)
                End If
            Next

            Select Case name
                Case "COUNT" : Return CLng(values.Count)

                Case "SUM", "AVG"
                    If values.Count = 0 Then
                        Return Nothing
                    End If

                    Dim sum As Double = 0

                    For Each v In values
                        sum += ToDouble(v)
                    Next

                    If name = "SUM" Then
                        Return sum
                    End If

                    Return sum / values.Count

                Case "MIN", "MAX"
                    If values.Count = 0 Then
                        Return Nothing
                    End If

                    Dim best As Object = values(0)

                    For Each v In values
                        Dim cmp As Integer = SqlTypes.CompareValues(v, best)

                        If (name = "MIN" AndAlso cmp < 0) OrElse (name = "MAX" AndAlso cmp > 0) Then
                            best = v
                        End If
                    Next

                    Return best
            End Select

            Throw New SqlError("unsupported aggregate function: " & fn.Name)
        End Function

        Public Shared Function ToBoolean(v As Object) As Boolean
            If v Is Nothing Then
                Return False
            End If

            If TypeOf v Is Boolean Then
                Return CBool(v)
            End If

            If SqlTypes.IsNumericValue(v) Then
                Return Convert.ToDouble(v) <> 0
            End If

            Return Convert.ToBoolean(v)
        End Function

        Public Shared Function ToDouble(v As Object) As Double
            If v Is Nothing Then
                Return 0
            End If

            If TypeOf v Is Boolean Then
                Return If(CBool(v), 1.0, 0.0)
            End If

            If SqlTypes.IsNumericValue(v) Then
                Return Convert.ToDouble(v)
            End If

            Dim d As Double = 0

            If Double.TryParse(Convert.ToString(v), d) Then
                Return d
            End If

            Return 0
        End Function

        ''' <summary>
        ''' translate a mysql LIKE pattern into a .net regular expression
        ''' </summary>
        Public Shared Function LikeToRegex(pattern As String) As String
            Dim sb As New Text.StringBuilder("(?s)^")

            For Each c In pattern
                Select Case c
                    Case "%"c : sb.Append(".*")
                    Case "_"c : sb.Append(".")
                    Case Else : sb.Append(Regex.Escape(c.ToString()))
                End Select
            Next

            sb.Append("$")
            Return sb.ToString()
        End Function

        ''' <summary>
        ''' sql truth check: NULL is treated as false in filtering contexts
        ''' </summary>
        Public Shared Function IsTrue(v As Object) As Boolean
            Return v IsNot Nothing AndAlso ToBoolean(v)
        End Function
    End Class
End Namespace
