﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.UnitTests;
using Xunit;

namespace Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests.Semantics;

public sealed class SimpleLambdaParametersWithModifiersTests : SemanticModelTestBase
{
    [Fact]
    public void TestOneParameterWithRef()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = (ref x) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(RefKind.Ref, symbol.Parameters.Single().RefKind);
        Assert.Equal(SpecialType.System_Int32, symbol.Parameters.Single().Type.SpecialType);
    }

    [Fact]
    public void TestTwoParametersWithRef()
    {
        var compilation = CreateCompilation("""
            delegate void D(string s, ref int x);

            class C
            {
                void M()
                {
                    D d = (s, ref x) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Type.SpecialType: SpecialType.System_String, RefKind: RefKind.None }, { Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref }]);
    }

    [Fact]
    public void TestTwoParametersWithRefAndOptionalValue()
    {
        var compilation = CreateCompilation("""
            delegate void D(string s, ref int x);

            class C
            {
                void M()
                {
                    D d = (s, ref x = 1) => { };
                }
            }
            """).VerifyDiagnostics(
                // (7,19): error CS1741: A ref or out parameter cannot have a default value
                //         D d = (s, ref x = 1) => { };
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(7, 19),
                // (7,23): error CS9098: Implicitly typed lambda parameter 'x' cannot have a default value.
                //         D d = (s, ref x = 1) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "x").WithArguments("x").WithLocation(7, 23));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [
        { Type.SpecialType: SpecialType.System_String, RefKind: RefKind.None },
        { Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Fact]
    public void TestOneParameterWithAnAttribute()
    {
        var compilation = CreateCompilation("""
            using System;

            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = ([CLSCompliant(false)] ref x) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(
            symbol.Parameters.Single().GetAttributes().Single().AttributeClass,
            compilation.GetTypeByMetadataName(typeof(CLSCompliantAttribute).FullName).GetPublicSymbol());
    }

    [Fact]
    public void TestOneParameterWithAnAttributeAndDefaultValue()
    {
        var compilation = CreateCompilation("""
            using System;

            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = ([CLSCompliant(false)] ref x = 0) => { };
                }
            }
            """).VerifyDiagnostics(
                // (9,38): error CS1741: A ref or out parameter cannot have a default value
                //         D d = ([CLSCompliant(false)] ref x = 0) => { };
                Diagnostic(ErrorCode.ERR_RefOutDefaultValue, "ref").WithLocation(9, 38),
                // (9,42): error CS9098: Implicitly typed lambda parameter 'x' cannot have a default value.
                //         D d = ([CLSCompliant(false)] ref x = 0) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "x").WithArguments("x").WithLocation(9, 42));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(
            symbol.Parameters.Single().GetAttributes().Single().AttributeClass,
            compilation.GetTypeByMetadataName(typeof(CLSCompliantAttribute).FullName).GetPublicSymbol());
        Assert.True(symbol.Parameters is [{ Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Theory]
    [InlineData("[CLSCompliant(false), My]")]
    [InlineData("[CLSCompliant(false)][My]")]
    public void TestOneParameterWithMultipleAttribute(string attributeForm)
    {
        var compilation = CreateCompilation($$"""
            using System;

            [AttributeUsage(AttributeTargets.Parameter)]
            class MyAttribute : Attribute;
            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = ({{attributeForm}} ref x) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(
            symbol.Parameters.Single().GetAttributes().Any(a => a.AttributeClass!.Equals(
                compilation.GetTypeByMetadataName(typeof(CLSCompliantAttribute).FullName).GetPublicSymbol())));
        Assert.True(
            symbol.Parameters.Single().GetAttributes().Any(a => a.AttributeClass!.Equals(
                compilation.GetTypeByMetadataName("MyAttribute").GetPublicSymbol())));
    }

    [Fact]
    public void TestOneParameterWithScoped()
    {
        var compilation = CreateCompilationWithSpan("""
            using System;
            delegate void D(scoped ReadOnlySpan<int> x);

            class C
            {
                void M()
                {
                    D d = (scoped x) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(ScopedKind.ScopedValue, symbol.Parameters.Single().ScopedKind);
        Assert.Equal(compilation.GetTypeByMetadataName(typeof(ReadOnlySpan<>).FullName).GetPublicSymbol(), symbol.Parameters.Single().Type.OriginalDefinition);
    }

    [Fact]
    public void TestOneParameterWithScopedAndOptionalValue()
    {
        var compilation = CreateCompilationWithSpan("""
            using System;
            delegate void D(scoped ReadOnlySpan<int> x);

            class C
            {
                void M()
                {
                    D d = (scoped x = default) => { };
                }
            }
            """).VerifyDiagnostics(
                // (8,23): error CS9098: Implicitly typed lambda parameter 'x' cannot have a default value.
                //         D d = (scoped x = default) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "x").WithArguments("x").WithLocation(8, 23),
                // (8,23): warning CS9099: Parameter 1 has default value 'null' in lambda but '<missing>' in the target delegate type.
                //         D d = (scoped x = default) => { };
                Diagnostic(ErrorCode.WRN_OptionalParamValueMismatch, "x").WithArguments("1", "null", "<missing>").WithLocation(8, 23));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(ScopedKind.ScopedValue, symbol.Parameters.Single().ScopedKind);
        Assert.True(symbol.Parameters.Single().IsOptional);
        Assert.Equal(compilation.GetTypeByMetadataName(typeof(ReadOnlySpan<>).FullName).GetPublicSymbol(), symbol.Parameters.Single().Type.OriginalDefinition);
    }

    [Theory, CombinatorialData]
    public void TestOneParameterWithScopedAsParameterName(bool escaped)
    {
        var compilation = CreateCompilationWithSpan($$"""
            using System;
            delegate void D(scoped ReadOnlySpan<int> x);

            class C
            {
                void M()
                {
                    D d = (scoped {{(escaped ? "@" : "")}}scoped) => { };
                }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.Equal(ScopedKind.ScopedValue, symbol.Parameters.Single().ScopedKind);
        Assert.Equal("scoped", symbol.Parameters.Single().Name);
        Assert.Equal(compilation.GetTypeByMetadataName(typeof(ReadOnlySpan<>).FullName).GetPublicSymbol(), symbol.Parameters.Single().Type.OriginalDefinition);
    }

    [Fact]
    public void TestInconsistentUseOfTypes()
    {
        var compilation = CreateCompilation("""
            delegate void D(string s, ref int x);

            class C
            {
                void M()
                {
                    D d = (string s, ref x) => { };
                }
            }
            """).VerifyDiagnostics(
                // (7,30): error CS0748: Inconsistent lambda parameter usage; parameter types must be all explicit or all implicit
                //         D d = (string s, ref x) => { };
                Diagnostic(ErrorCode.ERR_InconsistentLambdaParameterUsage, "x").WithLocation(7, 30));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [
        { Type.SpecialType: SpecialType.System_String, RefKind: RefKind.None },
        { Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref }]);
    }

    [Fact]
    public void TestOneParameterWithNoModifiersAndOptionalValue()
    {
        var compilation = CreateCompilation("""
            delegate void D(int x);

            class C
            {
                void M()
                {
                    D d = (x = 1) => { };
                }
            }
            """).VerifyDiagnostics(
                // (7,16): error CS9098: Implicitly typed lambda parameter 'x' cannot have a default value.
                //         D d = (x = 1) => { };
                Diagnostic(ErrorCode.ERR_ImplicitlyTypedDefaultParameter, "x").WithArguments("x").WithLocation(7, 16),
                // (7,16): warning CS9099: Parameter 1 has default value '1' in lambda but '<missing>' in the target delegate type.
                //         D d = (x = 1) => { };
                Diagnostic(ErrorCode.WRN_OptionalParamValueMismatch, "x").WithArguments("1", "1", "<missing>").WithLocation(7, 16));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.None, IsOptional: true }]);
    }

    [Fact]
    public void TestNonParenthesizedLambdaPrecededByRef()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = ref x => { };
                }
            }
            """).VerifyDiagnostics(
                // (7,11): error CS8171: Cannot initialize a by-value variable with a reference
                //         D d = ref x => { };
                Diagnostic(ErrorCode.ERR_InitializeByValueVariableWithReference, "d = ref x => { }").WithLocation(7, 11),
                // (7,19): error CS1676: Parameter 1 must be declared with the 'ref' keyword
                //         D d = ref x => { };
                Diagnostic(ErrorCode.ERR_BadParamRef, "x").WithArguments("1", "ref").WithLocation(7, 19));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Type: IErrorTypeSymbol, RefKind: RefKind.None, IsOptional: false }]);
    }

    [Fact]
    public void TestRefParameterMissingName()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = (ref) => { };
                }
            }
            """).VerifyDiagnostics(
                // (7,19): error CS1001: Identifier expected
                //         D d = (ref) => { };
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ")").WithLocation(7, 19));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Name: "", Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Fact]
    public void TestAnonymousMethodWithRefParameter()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);

            class C
            {
                void M()
                {
                    D d = delegate (ref x) { };
                }
            }
            """).VerifyDiagnostics(
                // (7,29): error CS0246: The type or namespace name 'x' could not be found (are you missing a using directive or an assembly reference?)
                //         D d = delegate (ref x) { };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "x").WithArguments("x").WithLocation(7, 29),
                // (7,30): error CS1001: Identifier expected
                //         D d = delegate (ref x) { };
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ")").WithLocation(7, 30));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<AnonymousMethodExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Name: "", Type: IErrorTypeSymbol { Name: "x" }, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Fact]
    public void TestLocalFunctionWithRefParameter()
    {
        var compilation = CreateCompilation("""
            class C
            {
                void M()
                {
                    void LocalFunc(ref x) { };
                }
            }
            """).VerifyDiagnostics(
                // (5,14): warning CS8321: The local function 'LocalFunc' is declared but never used
                //         void LocalFunc(ref x) { };
                Diagnostic(ErrorCode.WRN_UnreferencedLocalFunction, "LocalFunc").WithArguments("LocalFunc").WithLocation(5, 14),
                // (5,28): error CS0246: The type or namespace name 'x' could not be found (are you missing a using directive or an assembly reference?)
                //         void LocalFunc(ref x) { };
                Diagnostic(ErrorCode.ERR_SingleTypeNameNotFound, "x").WithArguments("x").WithLocation(5, 28),
                // (5,29): error CS1001: Identifier expected
                //         void LocalFunc(ref x) { };
                Diagnostic(ErrorCode.ERR_IdentifierExpected, ")").WithLocation(5, 29));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = semanticModel.GetDeclaredSymbol(lambda)!;

        Assert.Equal(MethodKind.LocalFunction, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Name: "", Type: IErrorTypeSymbol { Name: "x" }, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Fact]
    public void TestOverloadResolution1()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);
            delegate void E(ref int x);

            class C
            {
                void M()
                {
                    M1((ref x) => { });
                }

                void M1(D d) { }
                void M1(E e) { }
            }
            """).VerifyDiagnostics(
                // (8,9): error CS0121: The call is ambiguous between the following methods or properties: 'C.M1(D)' and 'C.M1(E)'
                //         M1((ref x) => { });
                Diagnostic(ErrorCode.ERR_AmbigCall, "M1").WithArguments("C.M1(D)", "C.M1(E)").WithLocation(8, 9));

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Name: "x", Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref, IsOptional: false }]);
    }

    [Fact]
    public void TestOverloadResolution2()
    {
        var compilation = CreateCompilation("""
            delegate void D(ref int x);
            delegate void E(int x);

            class C
            {
                void M()
                {
                    M1((ref x) => { });
                }

                void M1(D d) { }
                void M1(E e) { }
            }
            """).VerifyDiagnostics();

        var tree = compilation.SyntaxTrees.Single();
        var root = tree.GetRoot();
        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();

        var semanticModel = compilation.GetSemanticModel(tree);
        var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(lambda).Symbol!;

        Assert.Equal(MethodKind.LambdaMethod, symbol.MethodKind);
        Assert.True(symbol.Parameters is [{ Name: "x", Type.SpecialType: SpecialType.System_Int32, RefKind: RefKind.Ref, IsOptional: false }]);
    }
}
