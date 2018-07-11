﻿// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2014 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

namespace WebSharper.UI.Templating

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation
open ProviderImplementation.ProvidedTypes

[<AutoOpen>]
module private Impl =
    open WebSharper.UI.Templating.AST

    module PT =
        type Ctx = ProvidedTypesContext
        type Type = ProvidedTypeDefinition
    type Doc = WebSharper.UI.Doc
    type Elt = WebSharper.UI.Elt
    type Attr = WebSharper.UI.Attr
    type View<'T> = WebSharper.UI.View<'T>
    type Var<'T> = WebSharper.UI.Var<'T>
    type UINVar = WebSharper.UI.Var
    type TemplateHole = WebSharper.UI.TemplateHole
    type DomElement = WebSharper.JavaScript.Dom.Element
    type CheckedInput<'T> = WebSharper.UI.Client.CheckedInput<'T>
    module RTC = Runtime.Client
    module RTS = Runtime.Server
    type TI = RTS.TemplateInstance
    type Builder = RTS.ProviderBuilder

    type Ctx =
        {
            Template : Template
            FileId : TemplateName
            Id : option<TemplateName>
            Path : option<string>
            InlineFileId : option<TemplateName>
            ServerLoad : ServerLoad
            AllTemplates : Map<string, Map<option<string>, Template>>
        }

    module XmlDoc =
        let TemplateType n =
            "Builder for the template " + n + "; fill more holes or finish it with .Doc()"
        module Type =
            let Template n =
                let n = match n with "" -> "" | n -> " " + n
                "Builder for the template" + n + "; fill more holes or finish it with .Create()."
            let Instance =
                "An instance of the template; use .Doc to insert into the document."
            let Vars =
                "The reactive variables defined in this template."
        module Member =
            let Hole n =
                "Fill the hole \"" + n + "\" of the template."
            let Doc =
                "Get the Doc to insert this template instance into the page."
            let Var n =
                "Get the reactive variable \"" + n + "\" for this template instance."
            let Instance =
                "Create an instance of this template."
            let Doc_withUnfilled =
                """<summary>Get the Doc to insert this template instance into the page.</summary>
                    <param name="keepUnfilled">Server-side only: set to true to keep all unfilled holes, to be filled by the client with Bind.</param>"""
            let Bind =
                "Bind the template instance to the document."

    let IsExprType =
        let n = typeof<Expr>.FullName
        fun (x: Type) ->
            // Work around https://github.com/fsprojects/FSharp.TypeProviders.SDK/issues/236
            let x = if x.IsGenericType then x.GetGenericTypeDefinition() else x
            x.FullName.StartsWith n

    let BuildMethod'' (holeName: HoleName) (param: list<ProvidedParameter>) (resTy: Type)
            line column (ctx: Ctx) (wrapArgs: Expr<Builder> -> list<Expr> -> Expr<TemplateHole>) =
        let m =
            ProvidedMethod(holeName, param, resTy, function
                | this :: args ->
                    let var = Var("this", typeof<Builder>)
                    Expr.Let(var, <@ (%%this : obj) :?> Builder @>,
                        let this : Expr<Builder> = Expr.Var(var) |> Expr.Cast
                        <@@ box ((%this).WithHole(%wrapArgs this args)) @@>)
                | _ -> failwith "Incorrect invoke")
                .WithXmlDoc(XmlDoc.Member.Hole holeName)
        match ctx.Path with
        | Some p -> m.AddDefinitionLocation(line, column, p)
        | None -> ()
        m :> MemberInfo

    let BuildMethod' holeName argTy resTy line column ctx wrapArg =
        let isRefl = IsExprType argTy
        let param = ProvidedParameter(holeName, argTy, IsReflectedDefinition = isRefl)
        BuildMethod'' holeName [param] resTy line column ctx (fun st args -> wrapArg st (List.head args))

    let BuildMethod<'T> (holeName: HoleName) (resTy: Type)
            line column (ctx: Ctx) (wrapArg: Expr<Builder> -> Expr<'T> -> Expr<TemplateHole>) =
        let wrapArg a b = wrapArg a (Expr.Cast b)
        BuildMethod' holeName typeof<'T> resTy line column ctx wrapArg

    let BuildHoleMethods (holeName: HoleName) (holeDef: HoleDefinition) (resTy: Type) (varsTy: Type) (ctx: Ctx) : list<MemberInfo> =
        let mk wrapArg = BuildMethod holeName resTy holeDef.Line holeDef.Column ctx wrapArg
        let mkVar (wrapArg: Expr<Builder> -> Expr<Var<'T>> -> Expr<TemplateHole>) =
            let varMakeMeth =
                let viewTy = typedefof<View<_>>.MakeGenericType(typeof<'T>)
                let setterTy = typedefof<FSharpFunc<_,_>>.MakeGenericType(typeof<'T>, typeof<unit>)
                let param = [ProvidedParameter("view", viewTy); ProvidedParameter("setter", setterTy)]
                BuildMethod'' holeName param resTy holeDef.Line holeDef.Column ctx <| fun st args ->
                    match args with
                    | [view; setter] -> wrapArg st <@ UINVar.Make %%view %%setter @>
                    | _ -> failwith "Incorrect invoke"
            [mk wrapArg; varMakeMeth]
        let mkParamArray (wrapArg: _ -> Expr<'T[]> -> _) =
            let param = ProvidedParameter(holeName, typeof<'T>.MakeArrayType(), IsParamArray = true)
            BuildMethod'' holeName [param] resTy holeDef.Line holeDef.Column ctx (fun st args -> wrapArg st (List.head args |> Expr.Cast))
        let holeName' = holeName.ToLowerInvariant()
        let rec build : _ -> list<MemberInfo> = function
            | HoleKind.Attr ->
                [
                    mk <| fun _ (x: Expr<Attr>) ->
                        <@ TemplateHole.Attribute(holeName', %x) @>
                    mk <| fun _ (x: Expr<seq<Attr>>) ->
                        <@ TemplateHole.Attribute(holeName', Attr.Concat %x) @>
                    mkParamArray <| fun _ (x: Expr<Attr[]>) ->
                        <@ TemplateHole.Attribute(holeName', Attr.Concat %x) @>
                ]
            | HoleKind.Doc ->
                [
                    mk <| fun _ (x: Expr<Doc>) ->
                        <@ TemplateHole.Elt(holeName', %x) @>
                    mk <| fun _ (x: Expr<seq<Doc>>) ->
                        <@ TemplateHole.Elt(holeName', Doc.Concat %x) @>
                    mkParamArray <| fun _ (x: Expr<Doc[]>) ->
                        <@ TemplateHole.Elt(holeName', Doc.Concat %x) @>
                    mk <| fun _ (x: Expr<string>) ->
                        <@ TemplateHole.MakeText(holeName', %x) @>
                    mk <| fun _ (x: Expr<View<string>>) ->
                        <@ TemplateHole.TextView(holeName', %x) @>
                ]
            | HoleKind.ElemHandler ->
                [
                    mk <| fun _ (x: Expr<Expr<DomElement -> unit>>) ->
                        <@ RTC.AfterRenderQ(holeName', %x) @>
                    mk <| fun _ (x: Expr<Expr<unit -> unit>>) ->
                        <@ RTC.AfterRenderQ2(holeName', %x) @>
                ]
            | HoleKind.Event eventType ->
                let exprTy t = typedefof<Expr<_>>.MakeGenericType [| t |]
                let (^->) t u = typedefof<FSharpFunc<_, _>>.MakeGenericType [| t; u |]
                let evTy =
                    let a = typeof<WebSharper.JavaScript.Dom.Event>.Assembly
                    a.GetType("WebSharper.JavaScript.Dom." + eventType)
                let templateEventTy t u = typedefof<RTS.TemplateEvent<_,_>>.MakeGenericType [| t; u |]
                [
                    BuildMethod' holeName (exprTy (templateEventTy varsTy evTy ^-> typeof<unit>)) resTy holeDef.Line holeDef.Column ctx (fun e x ->
                        Expr.Call(typeof<RTS.Handler>.GetMethod("EventQ2").MakeGenericMethod(evTy),
                            [
                                <@ (%e).Key @>
                                <@ holeName' @>
                                <@ fun () -> (%e).Instance @>
                                x
                            ])
                        |> Expr.Cast
                    )
                ]
            | HoleKind.Simple ->
                [
                    mk <| fun _ (x: Expr<string>) ->
                        <@ TemplateHole.MakeText(holeName', %x) @>
                    mk <| fun _ (x: Expr<View<string>>) ->
                        <@ TemplateHole.TextView(holeName', %x) @>
                ]
            | HoleKind.Var (ValTy.Any | ValTy.String) ->
                [
                    yield! mkVar <| fun _ (x: Expr<Var<string>>) ->
                        <@ TemplateHole.VarStr(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<string>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                ]
            | HoleKind.Var ValTy.Number ->
                [
                    yield! mkVar <| fun _ (x: Expr<Var<int>>) ->
                        <@ TemplateHole.VarIntUnchecked(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<int>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                    yield! mkVar <| fun _ (x: Expr<Var<CheckedInput<int>>>) ->
                        <@ TemplateHole.VarInt(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<CheckedInput<int>>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                    yield! mkVar <| fun _ (x: Expr<Var<float>>) ->
                        <@ TemplateHole.VarFloatUnchecked(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<float>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                    yield! mkVar <| fun _ (x: Expr<Var<CheckedInput<float>>>) ->
                        <@ TemplateHole.VarFloat(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<CheckedInput<float>>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                ]
            | HoleKind.Var ValTy.Bool ->
                [
                    yield! mkVar <| fun _ (x: Expr<Var<bool>>) ->
                        <@ TemplateHole.VarBool(holeName', %x) @>
                    yield mk <| fun _ (x: Expr<bool>) ->
                        <@ TemplateHole.MakeVarLens(holeName', %x) @>
                ]
            | HoleKind.Mapped (kind = k) -> build k
            | HoleKind.Unknown -> failwithf "Error: Unknown HoleKind: %s" holeName
        build holeDef.Kind

    let OptionValue (x: option<'T>) : Expr<option<'T>> =
        match x with
        | None -> <@ None @>
        | Some x -> <@ Some (%%Expr.Value x : 'T) @>

    let References (ctx: Ctx) =
        Expr.NewArray(typeof<string * option<string> * string>,
            [ for (fileId, templateId) in ctx.Template.References do
                let src =
                    match ctx.AllTemplates.TryFind fileId with
                    | Some m ->
                        match m.TryFind templateId with
                        | Some t -> t.Src
                        | None -> failwithf "Template %A not found in file %A" templateId fileId
                    | None -> failwithf "File %A not found, expecting it with template %A" fileId templateId
                yield Expr.NewTuple [
                    Expr.Value fileId
                    OptionValue templateId
                    Expr.Value src
                ]
            ]
        )

    let InstanceVars (ctx: Ctx) =
        Expr.NewArray(typeof<string * RTS.ValTy>,
            [
                for KeyValue(holeName, holeDef) in ctx.Template.Holes do
                    let holeName' = holeName.ToLowerInvariant()
                    match holeDef.Kind with
                    | HoleKind.Var AST.ValTy.Any
                    | HoleKind.Var AST.ValTy.String -> yield <@@ (holeName', RTS.ValTy.String) @@>
                    | HoleKind.Var AST.ValTy.Number -> yield <@@ (holeName', RTS.ValTy.Number) @@>
                    | HoleKind.Var AST.ValTy.Bool -> yield <@@ (holeName', RTS.ValTy.Bool) @@>
                    | _ -> ()
            ]
        )

    let BindBody (ctx: Ctx) (args: list<Expr>) =
        let vars = InstanceVars ctx
        <@@ let builder = (%%args.[0] : obj) :?> Builder
            let holes, completed = RTS.Handler.CompleteHoles(builder.Key, builder.Holes, %%vars)
            let doc = RTS.Runtime.RunTemplate holes
            let _ = builder.SetInstance(TI(completed, doc))
            () @@>

    let InstanceBody (ctx: Ctx) (args: list<Expr>) =
        let name = ctx.Id |> Option.map (fun s -> s.ToLowerInvariant())
        let references = References ctx
        let vars = InstanceVars ctx
        <@@ let builder = (%%args.[0] : obj) :?> Builder
            let holes, completed = RTS.Handler.CompleteHoles(builder.Key, builder.Holes, %%vars)
            let doc =
                RTS.Runtime.GetOrLoadTemplate(
                    %%Expr.Value ctx.FileId,
                    %OptionValue name,
                    %OptionValue ctx.Path,
                    %%Expr.Value ctx.Template.Src,
                    holes,
                    %OptionValue ctx.InlineFileId,
                    %%Expr.Value ctx.ServerLoad,
                    %%references,
                    completed,
                    %%Expr.Value ctx.Template.IsElt,
                    %%(match args with _::keepUnfilled::_ -> keepUnfilled | _ -> Expr.Value false)
                )
            builder.SetInstance(TI(completed, doc)) @@>

    let BuildInstanceType (ty: PT.Type) (ctx: Ctx) =
        let res =
            ProvidedTypeDefinition("Instance", Some typeof<TI>)
                .WithXmlDoc(XmlDoc.Type.Instance)
                .AddTo(ty)
        let vars =
            ProvidedTypeDefinition("Vars", Some typeof<obj>)
                .WithXmlDoc(XmlDoc.Type.Vars)
                .AddTo(ty)
        vars.AddMembers [
            for KeyValue(holeName, def) in ctx.Template.Holes do
                let holeName' = holeName.ToLowerInvariant()
                match def.Kind with
                | AST.HoleKind.Var AST.ValTy.Any | AST.HoleKind.Var AST.ValTy.String ->
                    yield ProvidedProperty(holeName, typeof<Var<string>>, fun x -> <@@ ((%%x.[0] : obj) :?> TI).Hole holeName' @@>)
                        .WithXmlDoc(XmlDoc.Member.Var holeName)
                | AST.HoleKind.Var AST.ValTy.Number ->
                    yield ProvidedProperty(holeName, typeof<Var<float>>, fun x -> <@@ ((%%x.[0] : obj) :?> TI).Hole holeName' @@>)
                        .WithXmlDoc(XmlDoc.Member.Var holeName)
                | AST.HoleKind.Var AST.ValTy.Bool ->
                    yield ProvidedProperty(holeName, typeof<Var<bool>>, fun x -> <@@ ((%%x.[0] : obj) :?> TI).Hole holeName' @@>)
                        .WithXmlDoc(XmlDoc.Member.Var holeName)
                | _ -> ()
        ]
        res.AddMembers [
            yield ProvidedProperty("Doc", typeof<Doc>, fun x -> <@@ (%%x.[0] : TI).Doc @@>)
                .WithXmlDoc(XmlDoc.Member.Doc)
            if ctx.Template.IsElt then
                yield ProvidedProperty("Elt", typeof<Elt>, fun x -> <@@ (%%x.[0] : TI).Doc :?> Elt @@>)
                    .WithXmlDoc(XmlDoc.Member.Doc)
            yield ProvidedProperty("Vars", vars, fun x -> <@@ (%%x.[0] : TI) :> obj @@>)
                .WithXmlDoc(XmlDoc.Type.Vars)
        ]
        res, vars

    let BuildOneTemplate (ty: PT.Type) (isRoot: bool) (ctx: Ctx) =
        ty.AddMembers [
            let instanceTy, varsTy = BuildInstanceType ty ctx
            for KeyValue (holeName, holeKind) in ctx.Template.Holes do
                yield! BuildHoleMethods holeName holeKind ty varsTy ctx
            if isRoot then
                yield ProvidedMethod("Bind", [], typeof<unit>, BindBody ctx)
                    .WithXmlDoc(XmlDoc.Member.Bind) :> _
            else
                yield ProvidedMethod("Create", [], instanceTy, InstanceBody ctx)
                    .WithXmlDoc(XmlDoc.Member.Instance) :> _
            let docParams, docXmldoc =
                if isRoot then
                    [ProvidedParameter("keepUnfilled", typeof<bool>, optionalValue = box false)], XmlDoc.Member.Doc_withUnfilled
                else [], XmlDoc.Member.Doc
            yield ProvidedMethod("Doc", docParams, typeof<Doc>, fun args ->
                <@@ (%%InstanceBody ctx args : TI).Doc @@>)
                .WithXmlDoc(docXmldoc) :> _
            if ctx.Template.IsElt then
                yield ProvidedMethod("Elt", docParams, typeof<Elt>, fun args ->
                    <@@ (%%InstanceBody ctx args : TI).Doc :?> Elt @@>)
                    .WithXmlDoc(docXmldoc) :> _
            let ctor =
                ProvidedConstructor([], fun _ -> <@@ box (Builder.Make()) @@>)
            match ctx.Path with
            | Some path -> ctor.AddDefinitionLocation(ctx.Template.Line, ctx.Template.Column, path)
            | None -> ()
            yield ctor :> _
        ]

    let BuildOneFile (item: Parsing.ParseItem)
            (allTemplates: Map<string, Map<option<string>, Template>>)
            (containerTy: PT.Type)
            (inlineFileId: option<string>) =
        let baseId =
            match item.Id with
            | "" -> "t" + string (Guid.NewGuid().ToString("N"))
            | p -> p
        for KeyValue (tn, t) in item.Templates do
            let ctx = {
                Template = t
                FileId = baseId; Id = tn.IdAsOption; Path = item.Path
                InlineFileId = inlineFileId; ServerLoad = item.ServerLoad
                AllTemplates = allTemplates
            }
            match tn.NameAsOption with
            | None ->
                BuildOneTemplate (containerTy.WithXmlDoc(XmlDoc.Type.Template ""))
                    (item.ClientLoad = ClientLoad.FromDocument) ctx
            | Some n ->
                let ty =
                    ProvidedTypeDefinition(n, Some typeof<obj>)
                        .WithXmlDoc(XmlDoc.Type.Template n)
                        .AddTo(containerTy)
                BuildOneTemplate ty false ctx

    let BuildTP (parsed: Parsing.ParseItem[]) (containerTy: PT.Type) =
        let allTemplates =
            Map [for p in parsed -> p.Id, Map [for KeyValue(tid, t) in p.Templates -> tid.IdAsOption, t]]
        let inlineFileId (item: Parsing.ParseItem) =
            match item.ClientLoad with
            | ClientLoad.FromDocument -> Some parsed.[0].Id
            | _ -> None
        match parsed with
        | [| item |] ->
            BuildOneFile item allTemplates containerTy (inlineFileId item)
        | items ->
            items |> Array.iter (fun item ->
                let containerTy =
                    match item.Name with
                    | None -> containerTy
                    | Some name ->
                        ProvidedTypeDefinition(name, Some typeof<obj>)
                            .AddTo(containerTy)
                BuildOneFile item allTemplates containerTy (inlineFileId item)
            )

type FileWatcher (invalidate: unit -> unit, disposing: IEvent<EventHandler, EventArgs>, cfg: TypeProviderConfig) =
    let watchers = Dictionary<string, FileSystemWatcher>()
    let watcherNotifyFilter =
        NotifyFilters.LastWrite ||| NotifyFilters.Security ||| NotifyFilters.FileName

    let invalidateFile (path: string) (watcher: FileSystemWatcher) =
        if watchers.Remove(path) then
            watcher.Dispose()
        invalidate()

    do  disposing.Add <| fun _ ->
            for watcher in watchers.Values do watcher.Dispose()
            watchers.Clear()

    member this.WatchPath(path) =
        let rootedPath =
            Path.Combine(cfg.ResolutionFolder, path)
            |> Path.GetFullPath // canonicalize so that Renamed test below works
        if not (watchers.ContainsKey rootedPath) then
            let watcher =
                new FileSystemWatcher(
                    Path.GetDirectoryName rootedPath, Path.GetFileName rootedPath,
                    NotifyFilter = watcherNotifyFilter, EnableRaisingEvents = true
                )
            let inv _ = invalidateFile rootedPath watcher
            watcher.Changed.Add inv
            watcher.Renamed.Add(fun e ->
                if e.FullPath = rootedPath then
                    // renaming _to_ this file
                    inv e
                // else renaming _from_ this file
            )
            watcher.Created.Add inv
            watchers.Add(rootedPath, watcher)

[<TypeProvider>]
type TemplatingProvider (cfg: TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces(cfg)

    let thisAssembly = Assembly.GetExecutingAssembly()
    let rootNamespace = "WebSharper.UI.Templating"
    let templateTy = ProvidedTypeDefinition(thisAssembly, rootNamespace, "Template", Some typeof<obj>)

    let fileWatcher = FileWatcher(this.Invalidate, this.Disposing, cfg)

    let setupWatcher = function
        | Parsing.ParseKind.Inline -> ()
        | Parsing.ParseKind.Files paths -> Array.iter fileWatcher.WatchPath paths

    let setupTP () =
        templateTy.DefineStaticParameters(
            [
                ProvidedStaticParameter("pathOrHtml", typeof<string>)
                    .WithXmlDoc("Inline HTML or a path to an HTML file")
                ProvidedStaticParameter("clientLoad", typeof<ClientLoad>, ClientLoad.Inline)
                    .WithXmlDoc("Decide how the HTML is loaded when the template is used on the client side")
                ProvidedStaticParameter("serverLoad", typeof<ServerLoad>, ServerLoad.WhenChanged)
                    .WithXmlDoc("Decide how the HTML is loaded when the template is used on the server side")
                ProvidedStaticParameter("legacyMode", typeof<LegacyMode>, LegacyMode.Both)
                    .WithXmlDoc("Use WebSharper 3 or Zafir templating engine or both")
            ],
            fun typename pars ->
            try
                let (|ClientLoad|) (o: obj) =
                    match o with
                    | :? ClientLoad as clientLoad -> clientLoad
                    | :? int as clientLoad -> enum clientLoad
                    | _ ->  failwithf "Expecting a ClientLoad or int static parameter value for clientLoad"
                let (|ServerLoad|) (o: obj) =
                    match o with
                    | :? ServerLoad as serverLoad -> serverLoad
                    | :? int as serverLoad -> enum serverLoad
                    | _ ->  failwithf "Expecting a ServerLoad or int static parameter value for serverLoad"
                let (|LegacyMode|) (o: obj) =
                    match o with
                    | :? LegacyMode as legacyMode -> legacyMode
                    | :? int as legacyMode -> enum legacyMode
                    | _ ->  failwithf "Expecting a LegacyMode or int static parameter value for legacyMode"
                let pathOrHtml, clientLoad, serverLoad, legacyMode =
                    match pars with
                    | [| :? string as pathOrHtml; ClientLoad clientLoad; ServerLoad serverLoad; LegacyMode legacyMode |] ->
                        pathOrHtml, clientLoad, serverLoad, legacyMode
                    | a -> failwithf "Unexpected parameter values: %A" a
                let ty = //lazy (
                    let parsed = Parsing.Parse pathOrHtml cfg.ResolutionFolder serverLoad clientLoad
                    setupWatcher parsed.ParseKind
                    let ty =
                        ProvidedTypeDefinition(thisAssembly, rootNamespace, typename, Some typeof<obj>)
                            .WithXmlDoc(XmlDoc.TemplateType "")
                    match legacyMode with
                    | LegacyMode.Both ->
                        try OldProvider.RunOldProvider true pathOrHtml cfg ty
                        with _ -> ()
                        BuildTP parsed.Items ty
                    | LegacyMode.Old ->
                        OldProvider.RunOldProvider false pathOrHtml cfg ty
                    | _ ->
                        BuildTP parsed.Items ty
                    ty
                //)
                //cache.AddOrGetExisting(typename, ty)
                ty
            with e -> failwithf "%s %s" e.Message e.StackTrace
        )
        this.AddNamespace(rootNamespace, [templateTy])

    do setupTP()

    override this.ResolveAssembly(args) =
        //eprintfn "Type provider looking for assembly: %s" args.Name
        let name = AssemblyName(args.Name).Name.ToLowerInvariant()
        let an =
            cfg.ReferencedAssemblies
            |> Seq.tryFind (fun an ->
                Path.GetFileNameWithoutExtension(an).ToLowerInvariant() = name)
        match an with
        | Some f -> Assembly.LoadFrom f
        | None ->
            //eprintfn "Type provider didn't find assembly: %s" args.Name
            null

[<assembly:TypeProviderAssembly>]
do ()
