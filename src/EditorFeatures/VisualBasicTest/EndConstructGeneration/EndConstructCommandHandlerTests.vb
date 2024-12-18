﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.Editor.Implementation.EndConstructGeneration
Imports Microsoft.CodeAnalysis.Options
Imports Microsoft.VisualStudio.Text
Imports Microsoft.VisualStudio.Text.Editor
Imports Moq

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.EndConstructGeneration
    <[UseExportProvider]>
    <Trait(Traits.Feature, Traits.Features.EndConstructGeneration)>
    Public Class EndConstructCommandHandlerTests
        Private ReadOnly _endConstructServiceMock As New Mock(Of IEndConstructGenerationService)(MockBehavior.Strict)
        Private ReadOnly _textViewMock As New Mock(Of ITextView)(MockBehavior.Strict)
        Private ReadOnly _textBufferMock As New Mock(Of ITextBuffer)(MockBehavior.Strict)

#If False Then
        Private ReadOnly _featureOptions As New Mock(Of ILegacyWorkspaceOptionService)(MockBehavior.Strict)

        ' TODO(jasonmal): Figure out how to enable these tests.
        <WpfFact, Trait(Traits.Feature, Traits.Features.EndConstructGeneration)>
public async function ServiceNotCompletingShouldCallNextHandler()
            _endConstructServiceMock.Setup(Function(s) s.TryDo(It.IsAny(Of ITextView), It.IsAny(Of ITextBuffer), It.IsAny(Of Char))).Returns(False)
            _featureOptions.Setup(Function(s) s.GetOption(FeatureOnOffOptions.EndConstruct)).Returns(True)

            Dim nextHandlerCalled = False
            Dim handler As New EndConstructCommandHandler(_featureOptions.Object, _endConstructServiceMock.Object)
            handler.ExecuteCommand_ReturnKeyCommandHandler(New ReturnKeyCommandArgs(_textViewMock.Object, _textBufferMock.Object), Sub() nextHandlerCalled = True)

            Assert.True(nextHandlerCalled)
        End Sub

        <WpfFact, Trait(Traits.Feature, Traits.Features.EndConstructGeneration)>
public async function ServiceCompletingShouldCallNextHandler()
            _endConstructServiceMock.Setup(Function(s) s.TryDo(It.IsAny(Of ITextView), It.IsAny(Of ITextBuffer), It.IsAny(Of Char))).Returns(True)
            _featureOptions.Setup(Function(s) s.GetOption(FeatureOnOffOptions.EndConstruct)).Returns(True)

            Dim nextHandlerCalled = False
            Dim handler As New EndConstructCommandHandler(_featureOptions.Object, _endConstructServiceMock.Object)
            handler.ExecuteCommand_ReturnKeyCommandHandler(New ReturnKeyCommandArgs(_textViewMock.Object, _textBufferMock.Object), Sub() nextHandlerCalled = True)

            Assert.False(nextHandlerCalled)
        End Sub
#End If

        <WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544556")>
        Public Async Function EndConstruct_AfterCodeCleanup() As Task
            Dim code = <code>Class C
    Sub Main(args As String())
        Dim z = 1
        Dim y = 2
        If z &gt;&lt; y Then 
    End Sub
End Class</code>.Value.Replace(vbLf, vbCrLf)

            Dim expected = <code>Class C
    Sub Main(args As String())
        Dim z = 1
        Dim y = 2
        If z &lt;&gt; y Then 

        End If
    End Sub
End Class</code>.Value.Replace(vbLf, vbCrLf)

            VerifyAppliedAfterReturnUsingCommandHandler(code, {4, -1}, expected, {5, 12})
        End Function

        <WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546798")>
        Public Async Function EndConstruct_AfterCodeCleanup_FormatOnlyTouched() As Task
            Dim code = <code>Class C1
    Sub M1()
        System.Diagnostics. _Debug.Assert(True)
    End Sub
End Class</code>.Value.Replace(vbLf, vbCrLf)

            Dim expected = <code>Class C1
    Sub M1()
        System.Diagnostics. _
            Debug.Assert(True)
    End Sub
End Class</code>.Value.Replace(vbLf, vbCrLf)

            VerifyAppliedAfterReturnUsingCommandHandler(code, {2, 29}, expected, {3, 12})
        End Function

        <WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/531347")>
        Public Async Function EndConstruct_AfterCodeCleanup_FormatOnly_WhenContainsDiagnostics() As Task
            Dim code = <code>Module Program
    Sub Main(args As String())
        Dim a
        'Comment
        Dim b
        Dim c
    End Sub
End Module</code>.Value.Replace(vbLf, vbCrLf)

            Dim expected = <code>Module Program
    Sub Main(args As String())
        Dim a
        'Comment
        Dim b

        Dim c
    End Sub
End Module</code>.Value.Replace(vbLf, vbCrLf)

            VerifyAppliedAfterReturnUsingCommandHandler(code, {4, -1}, expected, {5, 8})
        End Function

        <WpfFact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/628656")>
        Public Async Function EndConstruct_NotOnLineFollowingToken() As Task
            Await VerifyStatementEndConstructNotAppliedAsync(
                text:="Class C

",
                caret:={2, 0})
            End Sub
    End Class
End Namespace
