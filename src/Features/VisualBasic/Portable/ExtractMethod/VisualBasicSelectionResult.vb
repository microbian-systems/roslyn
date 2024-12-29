﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.Collections.Immutable
Imports System.Threading
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.ExtractMethod
Imports Microsoft.CodeAnalysis.LanguageService
Imports Microsoft.CodeAnalysis.Text
Imports Microsoft.CodeAnalysis.VisualBasic
Imports Microsoft.CodeAnalysis.VisualBasic.LanguageService
Imports Microsoft.CodeAnalysis.VisualBasic.Symbols
Imports Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Microsoft.CodeAnalysis.VisualBasic.ExtractMethod
    Partial Friend NotInheritable Class VisualBasicExtractMethodService
        Friend NotInheritable Class VisualBasicSelectionResult
            Inherits SelectionResult

            Public Shared Async Function CreateResultAsync(
                    document As SemanticDocument,
                    selectionInfo As SelectionInfo,
                    selectionChanged As Boolean,
                    cancellationToken As CancellationToken) As Task(Of VisualBasicSelectionResult)

                Contract.ThrowIfNull(document)

                Dim root = document.Root
                Dim newDocument = Await SemanticDocument.CreateAsync(document.Document.WithSyntaxRoot(AddAnnotations(
                    root, {(selectionInfo.FirstTokenInFinalSpan, s_firstTokenAnnotation), (selectionInfo.LastTokenInFinalSpan, s_lastTokenAnnotation)})), cancellationToken).ConfigureAwait(False)

                Return New VisualBasicSelectionResult(
                    newDocument,
                    selectionInfo.GetSelectionType(),
                    selectionInfo.OriginalSpan,
                    selectionInfo.FinalSpan,
                    selectionChanged)
            End Function

            Private Sub New(
                document As SemanticDocument,
                selectionType As SelectionType,
                originalSpan As TextSpan,
                finalSpan As TextSpan,
                selectionChanged As Boolean)

                MyBase.New(
                    document,
                    selectionType,
                    originalSpan,
                    finalSpan,
                    selectionChanged)
            End Sub

            Protected Overrides ReadOnly Property SyntaxFacts As ISyntaxFacts = VisualBasicSyntaxFacts.Instance

            Protected Overrides Function UnderAnonymousOrLocalMethod(token As SyntaxToken, firstToken As SyntaxToken, lastToken As SyntaxToken) As Boolean
                Dim current = token.Parent

                While current IsNot Nothing
                    If TypeOf current Is DeclarationStatementSyntax OrElse
                       TypeOf current Is LambdaExpressionSyntax Then
                        Exit While
                    End If

                    current = current.Parent
                End While

                If current Is Nothing OrElse TypeOf current Is DeclarationStatementSyntax Then
                    Return False
                End If

                ' make sure selection contains the lambda
                Return firstToken.SpanStart <= current.GetFirstToken().SpanStart AndAlso
                       current.GetLastToken().Span.End <= lastToken.Span.End
            End Function

            Public Overrides Function GetOutermostCallSiteContainerToProcess(cancellationToken As CancellationToken) As SyntaxNode
                If Me.IsExtractMethodOnExpression Then
                    Dim container = Me.InnermostStatementContainer()

                    Contract.ThrowIfNull(container)
                    Contract.ThrowIfFalse(container.IsStatementContainerNode() OrElse
                                          TypeOf container Is TypeBlockSyntax OrElse
                                          TypeOf container Is CompilationUnitSyntax)

                    Return container
                ElseIf Me.IsExtractMethodOnSingleStatement() Then
                    Dim first = Me.GetFirstStatement()
                    Return first.Parent
                ElseIf Me.IsExtractMethodOnMultipleStatements() Then
                    Return Me.GetFirstStatementUnderContainer().Parent
                Else
                    Throw ExceptionUtilities.Unreachable()
                End If
            End Function

            Public Overrides Function ContainingScopeHasAsyncKeyword() As Boolean
                If IsExtractMethodOnExpression Then
                    Return False
                End If

                Dim node = Me.GetContainingScope()
                If TypeOf node Is MethodBlockBaseSyntax Then
                    Dim methodBlock = DirectCast(node, MethodBlockBaseSyntax)
                    If methodBlock.BlockStatement IsNot Nothing Then
                        Return methodBlock.BlockStatement.Modifiers.Any(SyntaxKind.AsyncKeyword)
                    End If

                    Return False
                ElseIf TypeOf node Is LambdaExpressionSyntax Then
                    Dim lambda = DirectCast(node, LambdaExpressionSyntax)
                    If lambda.SubOrFunctionHeader IsNot Nothing Then
                        Return lambda.SubOrFunctionHeader.Modifiers.Any(SyntaxKind.AsyncKeyword)
                    End If
                End If

                Return False
            End Function

            Public Overrides Function GetContainingScope() As SyntaxNode
                Contract.ThrowIfNull(Me.SemanticDocument)

                Dim first = GetFirstTokenInSelection()

                If IsExtractMethodOnExpression Then
                    Dim last = GetLastTokenInSelection()

                    Dim scope = first.GetCommonRoot(last).GetAncestorOrThis(Of ExpressionSyntax)()
                    Contract.ThrowIfNull(scope, "Should always find an expression given that SelectionInExpression was true")

                    Return VisualBasicSyntaxFacts.Instance.GetRootStandaloneExpression(scope)
                Else
                    ' it contains statements
                    Return first.GetAncestors(Of SyntaxNode).FirstOrDefault(Function(n) TypeOf n Is MethodBlockBaseSyntax OrElse TypeOf n Is LambdaExpressionSyntax)
                End If
            End Function

            Public Overrides Function GetReturnType() As (returnType As ITypeSymbol, returnsByRef As Boolean)
                ' Todo: consider supporting byref return types in VB
                Dim returnType = GetReturnTypeWorker()
                Return (returnType, returnsByRef:=False)
            End Function

            Private Function GetReturnTypeWorker() As ITypeSymbol
                Dim node = Me.GetContainingScope()
                Dim semanticModel = Me.SemanticDocument.SemanticModel

                ' special case for collection initializer and explicit cast
                If node.IsExpressionInCast() Then
                    Dim castExpression = TryCast(node.Parent, CastExpressionSyntax)
                    If castExpression IsNot Nothing Then
                        Return semanticModel.GetTypeInfo(castExpression.Type).Type
                    End If
                End If

                Dim expression As ExpressionSyntax
                If TypeOf node Is CollectionInitializerSyntax Then
                    expression = node.GetUnparenthesizedExpression()
                    Return semanticModel.GetTypeInfo(expression).ConvertedType
                End If

                Dim methodBlock = TryCast(node, MethodBlockBaseSyntax)
                If methodBlock IsNot Nothing Then
                    Dim symbol = semanticModel.GetDeclaredSymbol(methodBlock.BlockStatement)
                    Dim propertySymbol = TryCast(symbol, IPropertySymbol)
                    If propertySymbol IsNot Nothing Then
                        Return propertySymbol.Type
                    Else
                        Return DirectCast(symbol, IMethodSymbol).ReturnType
                    End If
                End If

                Dim info As TypeInfo
                Dim lambda = TryCast(node, LambdaExpressionSyntax)
                If lambda IsNot Nothing Then
                    If IsExtractMethodOnExpression Then
                        info = semanticModel.GetTypeInfo(lambda)
                        Return If(info.Type.IsObjectType(), info.ConvertedType, info.Type)
                    Else
                        Return semanticModel.GetLambdaOrAnonymousMethodReturnType(lambda)
                    End If
                End If

                expression = DirectCast(node, ExpressionSyntax)
                ' regular case. always use ConvertedType to get implicit conversion right.
                expression = expression.GetUnparenthesizedExpression()

                info = semanticModel.GetTypeInfo(expression)
                If info.ConvertedType IsNot Nothing AndAlso
                        Not info.ConvertedType.IsErrorType() Then
                    If expression.Kind = SyntaxKind.AddressOfExpression Then
                        Return info.ConvertedType
                    End If

                    Dim conversion = semanticModel.ClassifyConversion(expression, info.ConvertedType)
                    If conversion.IsNumeric AndAlso conversion.IsWidening Then
                        Return info.ConvertedType
                    End If

                    Dim conv = semanticModel.GetConversion(expression)
                    If IsCoClassImplicitConversion(info, conv, semanticModel.Compilation.CoClassType()) Then
                        Return info.ConvertedType
                    End If
                End If

                ' use FormattableString if conversion between String And FormattableString
                If If(info.Type?.SpecialType = SpecialType.System_String, False) AndAlso
                   info.ConvertedType?.IsFormattableStringOrIFormattable() Then

                    Return info.ConvertedType
                End If

                ' get type without considering implicit conversion
                Return If(info.Type.IsObjectType(), info.ConvertedType, info.Type)
            End Function

            Private Shared Function IsCoClassImplicitConversion(info As TypeInfo, conversion As Conversion, coclassSymbol As INamedTypeSymbol) As Boolean
                If Not conversion.IsWidening OrElse
                     info.ConvertedType Is Nothing OrElse
                     info.ConvertedType.TypeKind <> TypeKind.Interface Then
                    Return False
                End If

                ' let's see whether this interface has coclass attribute
                Return info.ConvertedType.HasAttribute(coclassSymbol)
            End Function

            Public Overrides Function GetFirstStatementUnderContainer() As ExecutableStatementSyntax
                Contract.ThrowIfTrue(IsExtractMethodOnExpression)

                Dim firstToken = GetFirstTokenInSelection()
                Dim lastToken = GetLastTokenInSelection()
                Dim commonRoot = firstToken.GetCommonRoot(lastToken)

                Dim statement As ExecutableStatementSyntax
                If commonRoot.IsStatementContainerNode() Then
                    Dim firstStatement = GetFirstStatement()
                    statement = firstStatement.GetAncestorsOrThis(Of ExecutableStatementSyntax) _
                                              .SkipWhile(Function(s) s.Parent IsNot commonRoot) _
                                              .First()
                    If statement.Parent.ContainStatement(statement) Then
                        Return statement
                    End If
                End If

                statement = commonRoot.GetStatementUnderContainer()
                Contract.ThrowIfNull(statement)

                Return statement
            End Function

            Public Overrides Function GetLastStatementUnderContainer() As ExecutableStatementSyntax
                Contract.ThrowIfTrue(IsExtractMethodOnExpression)

                Dim firstStatement = GetFirstStatementUnderContainer()
                Dim container = firstStatement.GetStatementContainer()

                Dim lastStatement = Me.GetLastStatement().
                    GetAncestorsOrThis(Of ExecutableStatementSyntax).
                    SkipWhile(Function(s) s.Parent IsNot container).
                    First()

                Contract.ThrowIfNull(lastStatement)
                Contract.ThrowIfFalse(lastStatement.Parent Is (GetFirstStatementUnderContainer()).Parent)

                Return lastStatement
            End Function

            Public Function InnermostStatementContainer() As SyntaxNode
                Contract.ThrowIfFalse(IsExtractMethodOnExpression)

                Dim containingScope = GetContainingScope()
                Dim statementContainer =
                    containingScope.Parent _
                                   .GetAncestorsOrThis(Of SyntaxNode)() _
                                   .FirstOrDefault(Function(n) n.IsStatementContainerNode)

                If statementContainer IsNot Nothing Then
                    Return statementContainer
                End If

                Dim field = containingScope.GetAncestor(Of FieldDeclarationSyntax)()
                If field IsNot Nothing Then
                    Return field.Parent
                End If

                Dim [property] = containingScope.GetAncestor(Of PropertyStatementSyntax)()
                If [property] IsNot Nothing Then
                    Return [property].Parent
                End If

                ' no repl yet
                ' Contract.ThrowIfFalse(last.IsParentKind(SyntaxKind.GlobalStatement))
                ' Contract.ThrowIfFalse(last.Parent.IsParentKind(SyntaxKind.CompilationUnit))
                ' Return last.Parent.Parent
                Throw ExceptionUtilities.Unreachable
            End Function

            Public Function IsUnderModuleBlock() As Boolean
                Dim currentScope = GetContainingScope()
                Dim types = currentScope.GetAncestors(Of TypeBlockSyntax)()

                Return types.Any(Function(t) t.BlockStatement.Kind = SyntaxKind.ModuleStatement)
            End Function

            Public Function ContainsInstanceExpression() As Boolean
                Dim first = GetFirstTokenInSelection()
                Dim last = GetLastTokenInSelection()
                Dim node = first.GetCommonRoot(last)

                Return node.DescendantNodesAndSelf(
                    TextSpan.FromBounds(first.SpanStart, last.Span.End)) _
                                           .Any(Function(n) TypeOf n Is InstanceExpressionSyntax)
            End Function
            Public Overrides Function ContainsNonReturnExitPointsStatements(jumpsOutOfRegion As ImmutableArray(Of SyntaxNode)) As Boolean
                Dim returnStatement = False
                Dim exitStatement = False

                For Each statement In jumpsOutOfRegion
                    If TypeOf statement Is ReturnStatementSyntax Then
                        returnStatement = True
                    ElseIf TypeOf statement Is ExitStatementSyntax Then
                        exitStatement = True
                    Else
                        Return True
                    End If
                Next

                If exitStatement Then
                    Return Not returnStatement
                End If

                Return False
            End Function

            Public Overrides Function GetOuterReturnStatements(commonRoot As SyntaxNode, jumpsOutOfRegionStatements As ImmutableArray(Of SyntaxNode)) As ImmutableArray(Of ExecutableStatementSyntax)
                Dim container = commonRoot.GetAncestorsOrThis(Of SyntaxNode)().Where(Function(a) a.IsReturnableConstruct()).FirstOrDefault()
                If container Is Nothing Then
                    Return ImmutableArray(Of ExecutableStatementSyntax).Empty
                End If

                ' now filter return statements to only include the one under outmost container
                Return jumpsOutOfRegionStatements.
                    OfType(Of ExecutableStatementSyntax).
                    Where(Function(n) TypeOf n Is ReturnStatementSyntax OrElse TypeOf n Is ExitStatementSyntax).
                    Select(Function(returnStatement) (returnStatement, container:=returnStatement.GetAncestors(Of SyntaxNode)().Where(Function(a) a.IsReturnableConstruct()).FirstOrDefault())).
                    Where(Function(p) p.container Is container).
                    SelectAsArray(Function(p) p.returnStatement)
            End Function

            Public Overrides Function IsFinalSpanSemanticallyValidSpan(
                    textSpan As TextSpan,
                    returnStatements As ImmutableArray(Of ExecutableStatementSyntax),
                    cancellationToken As CancellationToken) As Boolean

                ' do quick check to make sure we are under sub (no return value) container. otherwise, there is no point to anymore checks.
                If returnStatements.Any(Function(s)
                                            Return s.TypeSwitch(
                                                Function(e As ExitStatementSyntax) e.BlockKeyword.Kind <> SyntaxKind.SubKeyword,
                                                Function(r As ReturnStatementSyntax) r.Expression IsNot Nothing,
                                                Function(n As ExecutableStatementSyntax) True)
                                        End Function) Then
                    Return False
                End If

                ' check whether selection reaches the end of the container
                Dim lastToken = Me.SemanticDocument.Root.FindToken(textSpan.End)
                If lastToken.Kind = SyntaxKind.None Then
                    Return False
                End If

                Dim nextToken = lastToken.GetNextToken(includeZeroWidth:=True)

                Dim container = nextToken.GetAncestors(Of SyntaxNode).Where(Function(n) n.IsReturnableConstruct()).FirstOrDefault()
                If container Is Nothing Then
                    Return False
                End If

                Dim match = If(TryCast(container, MethodBlockBaseSyntax)?.EndBlockStatement.EndKeyword = nextToken, False) OrElse
                            If(TryCast(container, MultiLineLambdaExpressionSyntax)?.EndSubOrFunctionStatement.EndKeyword = nextToken, False)

                If Not match Then
                    Return False
                End If

                If TryCast(container, MethodBlockBaseSyntax)?.BlockStatement.Kind = SyntaxKind.SubStatement Then
                    Return True
                ElseIf TryCast(container, MultiLineLambdaExpressionSyntax)?.SubOrFunctionHeader.Kind = SyntaxKind.SubLambdaHeader Then
                    Return True
                Else
                    Return False
                End If
            End Function

            Protected Overrides Function ValidateLanguageSpecificRules(cancellationToken As CancellationToken) As OperationStatus
                Dim containsAllStaticLocals = ContainsAllStaticLocalUsagesDefinedInSelectionIfExist()
                If Not containsAllStaticLocals Then
                    Return New OperationStatus(succeeded:=True, VBFeaturesResources.all_static_local_usages_defined_in_the_selection_must_be_included_in_the_selection)
                End If

                Return OperationStatus.SucceededStatus
            End Function

            Private Function ContainsAllStaticLocalUsagesDefinedInSelectionIfExist() As Boolean
                If Me.GetFirstTokenInSelection().GetAncestor(Of FieldDeclarationSyntax)() IsNot Nothing OrElse
                   Me.GetFirstTokenInSelection().GetAncestor(Of PropertyStatementSyntax)() IsNot Nothing Then
                    ' static local can't exist in field initializer
                    Return True
                End If

                Dim result As DataFlowAnalysis

                Dim semanticModel = Me.SemanticDocument.SemanticModel
                If Me.IsExtractMethodOnExpression Then
                    Dim expression = Me.GetFirstTokenInSelection().GetCommonRoot(Me.GetLastTokenInSelection()).GetAncestorOrThis(Of ExpressionSyntax)()
                    result = SemanticModel.AnalyzeDataFlow(expression)
                Else
                    Dim range = Me.GetFlowAnalysisNodeRange()
                    result = SemanticModel.AnalyzeDataFlow(range.firstStatement, range.lastStatement)
                End If

                For Each symbol In result.VariablesDeclared
                    Dim local = TryCast(symbol, ILocalSymbol)
                    If local Is Nothing Then
                        Continue For
                    End If

                    If Not local.IsStatic Then
                        Continue For
                    End If

                    If result.WrittenOutside().Any(Function(s) Equals(s, local)) OrElse
                        result.ReadOutside().Any(Function(s) Equals(s, local)) Then
                        Return False
                    End If
                Next

                Return True
            End Function

        End Class
    End Class
End Namespace
