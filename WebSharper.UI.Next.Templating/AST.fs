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

module WebSharper.UI.Next.Templating.AST

open System.Text.RegularExpressions

type HoleName = string
type TemplateName = string

[<RequireQualifiedAccess>]
type ValTy =
    | Any
    | String
    | Number
    | Bool

[<RequireQualifiedAccess>]
type HoleKind =
    | Simple of valTy: ValTy
    | Var of valTy: ValTy
    | Attr
    | Doc
    | Event
    | ElemHandler

[<RequireQualifiedAccess>]
type StringPart =
    | Text of text: string
    | Hole of HoleName

[<RequireQualifiedAccess>]
type SpecialHole =
    | Scripts
    | Meta
    | Styles

[<RequireQualifiedAccess>]
type Attr =
    | Simple of name: string * value: string
    | Compound of name: string * value: StringPart[]
    | Attr of holeName: HoleName
    | Event of eventName: string * HoleName
    | OnAfterRender of HoleName
    | SpecialHole of SpecialHole * HoleName

[<RequireQualifiedAccess>]
type Node =
    | Text of StringPart[]
    | Element of nodeName: string * isSvg: bool * Attr[] * children: Node[]
    | Input of nodeName: string * var: HoleName * Attr[] * children: Node[]
    | DocHole of HoleName

type Template =
    {
        Holes : Map<HoleName, HoleKind>
        Value : Node[]
        Name : string
        Src : string
    }

let [<Literal>] TemplateAttr            = "ws-template"
let [<Literal>] ChildrenTemplateAttr    = "ws-children-template"
let [<Literal>] HoleAttr                = "ws-hole"
let [<Literal>] ReplaceAttr             = "ws-replace"
let [<Literal>] AttrAttr                = "ws-attr"
let [<Literal>] AfterRenderAttr         = "ws-onafterrender"
let [<Literal>] EventAttrPrefix         = "ws-on"
let [<Literal>] VarAttr                 = "ws-var"
let TextHoleRegex = Regex(@"\$\{([a-zA-Z_][-a-zA-Z0-9_]*)\}", RegexOptions.Compiled)
let HoleNameRegex = Regex(@"^[a-zA-Z_][-a-zA-Z0-9_]*$", RegexOptions.Compiled)

