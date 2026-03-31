using Graphity.Core.Analyzers.TypeScript;
using Graphity.Core.Graph;

namespace Graphity.Core.Tests.Analyzers;

public class TypeScriptAnalyzerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly TypeScriptAnalyzer _analyzer = new();

    public TypeScriptAnalyzerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "graphity_ts_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public async Task ExtractsNamedImports()
    {
        var filePath = CreateFile("src/app.ts", """
            import { Component, OnInit } from '@angular/core';
            import { UserService } from './services/user';
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var importEdges = result.Edges.Where(e => e.Type == EdgeType.Imports).ToList();
        Assert.Equal(3, importEdges.Count);
        Assert.Contains(importEdges, e => e.TargetId == "Module:@angular/core" && e.Reason!.Contains("Component"));
        Assert.Contains(importEdges, e => e.TargetId == "Module:@angular/core" && e.Reason!.Contains("OnInit"));
        Assert.Contains(importEdges, e => e.TargetId == "Module:./services/user");
    }

    [Fact]
    public async Task ExtractsDefaultImport()
    {
        var filePath = CreateFile("src/app.ts", """
            import React from 'react';
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var importEdges = result.Edges.Where(e => e.Type == EdgeType.Imports).ToList();
        Assert.Single(importEdges);
        Assert.Equal("Module:react", importEdges[0].TargetId);
        Assert.Contains("default", importEdges[0].Reason!);
    }

    [Fact]
    public async Task ExtractsNamespaceImport()
    {
        var filePath = CreateFile("src/app.ts", """
            import * as path from 'path';
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var importEdges = result.Edges.Where(e => e.Type == EdgeType.Imports).ToList();
        Assert.Single(importEdges);
        Assert.Equal("Module:path", importEdges[0].TargetId);
        Assert.Contains("path", importEdges[0].Reason!);
    }

    [Fact]
    public async Task ExtractsRequireImport()
    {
        var filePath = CreateFile("src/app.js", """
            const fs = require('fs');
            const path = require('path');
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var importEdges = result.Edges.Where(e => e.Type == EdgeType.Imports).ToList();
        Assert.Equal(2, importEdges.Count);
        Assert.Contains(importEdges, e => e.TargetId == "Module:fs");
        Assert.Contains(importEdges, e => e.TargetId == "Module:path");
    }

    [Fact]
    public async Task ExtractsClassWithExtendsAndImplements()
    {
        var filePath = CreateFile("src/models.ts", """
            export class UserService extends BaseService implements IUserService, IDisposable {
                constructor() {}
            }
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var classNodes = result.Nodes.Where(n => n.Type == NodeType.Class).ToList();
        Assert.Single(classNodes);
        Assert.Equal("UserService", classNodes[0].Name);
        Assert.True(classNodes[0].IsExported);
        Assert.Equal("typescript", classNodes[0].Language);

        var extendsEdges = result.Edges.Where(e => e.Type == EdgeType.Extends).ToList();
        Assert.Single(extendsEdges);
        Assert.Contains("BaseService", extendsEdges[0].TargetId);

        var implementsEdges = result.Edges.Where(e => e.Type == EdgeType.Implements).ToList();
        Assert.Equal(2, implementsEdges.Count);
        Assert.Contains(implementsEdges, e => e.TargetId.Contains("IUserService"));
        Assert.Contains(implementsEdges, e => e.TargetId.Contains("IDisposable"));
    }

    [Fact]
    public async Task ExtractsExportedFunctionDeclaration()
    {
        var filePath = CreateFile("src/utils.ts", """
            export function formatDate(date: Date): string {
                return date.toISOString();
            }

            function helperInternal() {
                return 42;
            }
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var funcNodes = result.Nodes.Where(n => n.Type == NodeType.Function).ToList();
        Assert.Equal(2, funcNodes.Count);

        var exported = funcNodes.First(n => n.Name == "formatDate");
        Assert.True(exported.IsExported);

        var internal_ = funcNodes.First(n => n.Name == "helperInternal");
        Assert.False(internal_.IsExported);
    }

    [Fact]
    public async Task ExtractsArrowFunction()
    {
        var filePath = CreateFile("src/handlers.ts", """
            export const handleClick = (event: Event) => {
                console.log(event);
            };

            const processData = async (data: any) => {
                return data;
            };
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var funcNodes = result.Nodes.Where(n => n.Type == NodeType.Function).ToList();
        Assert.Equal(2, funcNodes.Count);

        var handleClick = funcNodes.First(n => n.Name == "handleClick");
        Assert.True(handleClick.IsExported);

        var processData = funcNodes.First(n => n.Name == "processData");
        Assert.False(processData.IsExported);
    }

    [Fact]
    public async Task ExtractsEnums()
    {
        var filePath = CreateFile("src/types.ts", """
            export enum Color {
                Red,
                Green,
                Blue
            }

            export const enum Direction {
                Up,
                Down
            }
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var enumNodes = result.Nodes.Where(n => n.Type == NodeType.Enum).ToList();
        Assert.Equal(2, enumNodes.Count);
        Assert.Contains(enumNodes, n => n.Name == "Color" && n.IsExported);
        Assert.Contains(enumNodes, n => n.Name == "Direction" && n.IsExported);
        Assert.All(enumNodes, n => Assert.Equal("typescript", n.Language));
    }

    [Fact]
    public async Task ExtractsInterfaceWithExtends()
    {
        var filePath = CreateFile("src/interfaces.ts", """
            export interface IAnimal {
                name: string;
            }

            export interface IDog extends IAnimal, ITrainable {
                breed: string;
            }
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var ifaceNodes = result.Nodes.Where(n => n.Type == NodeType.Interface).ToList();
        Assert.Equal(2, ifaceNodes.Count);
        Assert.All(ifaceNodes, n => Assert.True(n.IsExported));

        var extendsEdges = result.Edges.Where(e => e.Type == EdgeType.Extends).ToList();
        Assert.Equal(2, extendsEdges.Count);
        Assert.Contains(extendsEdges, e => e.TargetId.Contains("IAnimal"));
        Assert.Contains(extendsEdges, e => e.TargetId.Contains("ITrainable"));
    }

    [Fact]
    public async Task MarksExportedSymbolsCorrectly()
    {
        var filePath = CreateFile("src/mixed.ts", """
            export class PublicClass {}
            class InternalClass {}
            export function publicFunc() {}
            function internalFunc() {}
            export interface IPublic {}
            interface IInternal {}
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var exported = result.Nodes.Where(n => n.IsExported).Select(n => n.Name).ToHashSet();
        var notExported = result.Nodes.Where(n => !n.IsExported).Select(n => n.Name).ToHashSet();

        Assert.Contains("PublicClass", exported);
        Assert.Contains("publicFunc", exported);
        Assert.Contains("IPublic", exported);

        Assert.Contains("InternalClass", notExported);
        Assert.Contains("internalFunc", notExported);
        Assert.Contains("IInternal", notExported);
    }

    [Fact]
    public async Task SetsLanguageToTypescript()
    {
        var filePath = CreateFile("src/app.ts", """
            export class App {}
            export function main() {}
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        Assert.All(result.Nodes, n => Assert.Equal("typescript", n.Language));
    }

    [Fact]
    public async Task CreatesDefinesEdgesFromFileToSymbols()
    {
        var filePath = CreateFile("src/app.ts", """
            export class MyClass {}
            export function myFunc() {}
            export enum MyEnum { A }
            export interface MyInterface {}
            """);

        var result = await _analyzer.AnalyzeAsync(filePath, _tempRoot);

        var definesEdges = result.Edges.Where(e => e.Type == EdgeType.Defines).ToList();
        Assert.Equal(4, definesEdges.Count);
        Assert.All(definesEdges, e => Assert.Equal("File:src/app.ts", e.SourceId));
    }

    [Fact]
    public void SupportedExtensionsIncludeTsJsVariants()
    {
        Assert.Contains(".ts", _analyzer.SupportedExtensions);
        Assert.Contains(".tsx", _analyzer.SupportedExtensions);
        Assert.Contains(".js", _analyzer.SupportedExtensions);
        Assert.Contains(".jsx", _analyzer.SupportedExtensions);
    }
}
