namespace FsAutoComplete

open System
open System.IO
open FSharp.Compiler
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Tokenization
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Position
open Utils
open FsAutoComplete.Utils

[<RequireQualifiedAccess>]
type internal FindDeclarationResult =
    | Range of FSharp.Compiler.Text.Range
    /// The declaration refers to a file.
    | File of string

type internal ParseAndCheckResults
    (
        parseResults: FSharpParseFileResults,
        checkResults: FSharpCheckFileResults,
        entityCache: EntityCache
    ) =

  member __.TryGetMethodOverrides (lines: LineStr[]) (pos: pos) = async {
    // Find the number of `,` in the current signature
    let commas, _, _ =
      let lineCutoff = pos.Line - 6
      let rec prevPos (line,col) =
        match line, col with
        | 1, 1
        | _ when line < lineCutoff -> 1, 1
        | _, 1 ->
           let prevLine = lines.[line - 2]
           if prevLine.Length = 0 then prevPos(line-1, 1)
           else line - 1, prevLine.Length
        | _    -> line, col - 1

      let rec loop commas depth (line, col) =
        if (line,col) <= (1,1) then (0, line, col) else
        let ch = lines.[line - 1].[col - 1]
        let commas = if depth = 0 && ch = ',' then commas + 1 else commas
        if (ch = '(' || ch = '{' || ch = '[') && depth > 0 then loop commas (depth - 1) (prevPos (line,col))
        elif ch = ')' || ch = '}' || ch = ']' then loop commas (depth + 1) (prevPos (line,col))
        elif ch = '(' || ch = '<' then commas, line, col
        else loop commas depth (prevPos (line,col))
      match loop 0 0 (prevPos(pos.Line, pos.Column)) with
      | _, 1, 1 -> 0, pos.Line, pos.Column
      | newPos -> newPos
    let testPos = mkPos pos.Line (pos.Column - 1)
    // Get the parameter locations
    let paramLocations = parseResults.FindParameterLocations pos
    match paramLocations with
    | None -> return ResultOrString.Error "Could not find parameter locations"
    | Some nwpl ->
      let names = nwpl.LongId
      let lidEnd = nwpl.LongIdEndLocation
      let meth = checkResults.GetMethods(lidEnd.Line, lidEnd.Column, "", Some names)
      return Ok(meth, commas) }

  member __.TryFindDeclaration (pos: pos) (lineStr: LineStr) = async {
    // try find identifier first
    let! identResult = __.TryFindIdentifierDeclaration pos lineStr
    match identResult with
    | Ok r -> return Ok r
    | Error identErr ->
    // then #load directive
    let! loadResult = __.TryFindLoadDirectiveSource pos lineStr
    match loadResult with
    | Ok r -> return Ok r
    | Error _ -> return Error identErr
  }

  member __.TryFindLoadDirectiveSource (pos: pos) (lineStr: LineStr) = async {
    let tryGetFullPath fileName =
      try
        // use the parsed file name directory as base path
        let basePath = Path.GetDirectoryName(__.FileName)
        Some (Path.Combine(basePath, fileName))
      with
      | :? ArgumentException -> None
      | :? PathTooLongException -> None
      | :? NotSupportedException -> None

    let result =
      InteractiveDirectives.tryParseLoad lineStr pos.Column
      |> Option.bind tryGetFullPath

    match result with
    | Some file -> return Ok (FindDeclarationResult.File file)
    | None -> return Error "load directive not recognized"
  }

  member __.TryFindIdentifierDeclaration (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return ResultOrString.Error "Could not find ident at this location"
    | Some((_, col), identIsland) ->
      let identIsland = Array.toList identIsland
      let declarations = checkResults.GetDeclarationLocation(pos.Line, col, lineStr, identIsland, false)

      /// these are all None because you can't easily get the source file from the external symbol information here.
      let tryGetSourceRangeForSymbol (sym: FindDeclExternalSymbol): (string * int * int) option =
        match sym with
        | FindDeclExternalSymbol.Type name -> None
        | FindDeclExternalSymbol.Constructor(typeName, args) -> None
        | FindDeclExternalSymbol.Method(typeName, name, paramSyms, genericArity) -> None
        | FindDeclExternalSymbol.Field(typeName, name) -> None
        | FindDeclExternalSymbol.Event(typeName, name) -> None
        | FindDeclExternalSymbol.Property(typeName, name) -> None

      // attempts to manually discover symbol use and externalsymbol information for a range that doesn't exist in a local file
      // bugfix/workaround for FCS returning invalid declfound for f# members.
      let tryRecoverExternalSymbolForNonexistentDecl (rangeInNonexistentFile: range): Async<ResultOrString<string * string>> = async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None -> return ResultOrString.Error (sprintf "Range for nonexistent file found, no ident found: %s" rangeInNonexistentFile.FileName)
        | Some ((_, col), identIsland) ->
          let identIsland = Array.toList identIsland
          let symbolUse = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)
          match symbolUse with
          | None -> return ResultOrString.Error (sprintf "Range for nonexistent file found, no symboluse found: %s" rangeInNonexistentFile.FileName)
          | Some sym ->
              return ResultOrString.Error (sprintf "Assembly '%s' declaring symbol '%s' has no location on disk" sym.Symbol.Assembly.QualifiedName sym.Symbol.DisplayName)
      }

      match declarations with
      | FindDeclResult.DeclNotFound reason ->
        let elaboration =
          match reason with
          | FindDeclFailureReason.NoSourceCode -> "No source code was found for the declaration"
          | FindDeclFailureReason.ProvidedMember m -> sprintf "Go-to-declaration is not available for Type Provider-provided member %s" m
          | FindDeclFailureReason.ProvidedType t -> sprintf "Go-to-declaration is not available from Type Provider-provided type %s" t
          | FindDeclFailureReason.Unknown r -> r
        return ResultOrString.Error (sprintf "Could not find declaration. %s" elaboration)
      | FindDeclResult.DeclFound range when range.FileName.EndsWith(Range.rangeStartup.FileName) -> return ResultOrString.Error "Could not find declaration"
      | FindDeclResult.DeclFound range when System.IO.File.Exists range.FileName ->
        let rangeStr = range.ToString()
        return Ok (FindDeclarationResult.Range range)
      | FindDeclResult.DeclFound rangeInNonexistentFile ->
        let range = rangeInNonexistentFile.ToString()
        match! tryRecoverExternalSymbolForNonexistentDecl rangeInNonexistentFile with
        | Ok (assemblyFile, sourceFile) ->
          return ResultOrString.Error "no reason"
        | Error e -> return Error e
      | FindDeclResult.ExternalDecl (assembly, externalSym) ->
        // not enough info on external symbols to get a range-like thing :(
        return ResultOrString.Error "no reason"
    }

  member __.TryFindTypeDeclaration (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None ->
      return Error "Cannot find ident at this location"
    | Some((_, col),identIsland) ->
      let identIsland = Array.toList identIsland
      let symbol = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)
      match symbol with
      | None ->
        return Error "Cannot find symbol at this location"
      | Some sym ->

        let tryGetTypeDef (t: FSharpType option) =
          t |> Option.bind (fun t -> if t.HasTypeDefinition then Some t.TypeDefinition else None)

        let rec tryGetSource (ty: FSharpEntity option) = async {
            match ty |> Option.map (fun ty -> ty, ty.DeclarationLocation) with
            | Some (_, loc) when File.Exists loc.FileName ->
                return Ok (FindDeclarationResult.Range loc)
            | Some (ty, loc) ->
                return! tryDecompile ty
            | None ->  
                return Error "No type information for the symbol at this location"
          }
        and tryDecompile (ty: FSharpEntity) = async {
            match ty.TryFullName with
            | Some fullName ->
                return ResultOrString.Error "none"
            | None ->
                // might be abbreviated type (like string)
                return!
                  if ty.IsFSharpAbbreviation then Some ty.AbbreviatedType else None
                  |> tryGetTypeDef
                  |> tryGetSource
          }

        let ty =
          match sym with
          | SymbolUse.Field f -> Some f.FieldType |> tryGetTypeDef
          | SymbolUse.Constructor c -> c.DeclaringEntity
          | SymbolUse.Property p when p.IsPropertyGetterMethod -> 
              Some p.ReturnParameter.Type |> tryGetTypeDef
          | SymbolUse.Val v -> v.FullTypeSafe |> tryGetTypeDef
          | SymbolUse.Entity (e, _) -> Some e
          | SymbolUse.UnionCase c -> Some c.ReturnType |> tryGetTypeDef
          | SymbolUse.Parameter p -> Some p.Type |> tryGetTypeDef
          | _ -> None
        
        return! tryGetSource ty
  }

  member __.TryGetToolTip (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return ResultOrString.Error "Cannot find ident for tooltip"
    | Some((startCol, endCol),identIsland) ->
      let identIsland = Array.toList identIsland
      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let tip = checkResults.GetToolTip(pos.Line, endCol, lineStr, identIsland, FSharpTokenTag.Identifier)
      return
        match tip with
        | ToolTipText(elems) when elems |> List.forall ((=) ToolTipElement.None) ->
            match identIsland with
            | [ident] ->
               match KeywordList.keywordTooltips.TryGetValue ident with
               | true, tip ->
                  Ok (startCol, endCol, tip)
               | _ ->
                  ResultOrString.Error "No tooltip information"
            | _ ->
              ResultOrString.Error "No tooltip information"
        | _ ->
          Ok(startCol, endCol, tip)
  }

  member __.TryGetToolTipEnhanced (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return Error "Cannot find ident for tooltip"
    | Some((startCol, endCol),identIsland) ->
      let identIsland = Array.toList identIsland
      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let tip = checkResults.GetToolTip(pos.Line, endCol, lineStr, identIsland, FSharpTokenTag.Identifier)
      let symbol = checkResults.GetSymbolUseAtLocation(pos.Line, endCol, lineStr, identIsland)

      match tip with
      | ToolTipText(elems) when elems |> List.forall ((=) ToolTipElement.None) && symbol.IsNone ->
          match identIsland with
          | [ident] ->
             match KeywordList.keywordTooltips.TryGetValue ident with
             | true, tip ->
                return Ok (startCol, endCol, tip, ident, "", None)
             | _ ->
                return Error "No tooltip information"
          | _ ->
            return Error "No tooltip information"
      | _ ->
      match symbol with
      | None ->
        return Error "No tooltip information"
      | Some symbol ->

        match SignatureFormatter.getTooltipDetailsFromSymbolUse symbol with
        | None ->
          return Error "No tooltip information"
        | Some (signature, footer) ->
            let typeDoc = getTypeIfConstructor symbol.Symbol |> Option.map (fun n -> n.XmlDocSig)
            return Ok (startCol, endCol, tip, signature, footer, typeDoc)
  }

  member __.TryGetFormattedDocumentation (pos: pos) (lineStr: LineStr) = async {
    match Lexer.findLongIdents(pos.Column - 1, lineStr) with
    | None -> return Error "Cannot find ident"
    | Some((_, col),identIsland) ->
      let identIsland = Array.toList identIsland
      // TODO: Display other tooltip types, for example for strings or comments where appropriate
      let tip = checkResults.GetToolTip(pos.Line, col, lineStr, identIsland, FSharpTokenTag.Identifier)
      let symbol = checkResults.GetSymbolUseAtLocation(pos.Line, col, lineStr, identIsland)

      match tip with
      | ToolTipText(elems) when elems |> List.forall ((=) ToolTipElement.None) && symbol.IsNone ->
          match identIsland with
          | [ident] ->
             match KeywordList.keywordTooltips.TryGetValue ident with
             | true, tip ->
                return Ok (Some tip, None, (ident, (DocumentationFormatter.emptyTypeTip)), "", "")
             | _ ->
                return Error "No tooltip information"
          | _ ->
            return Error "No documentation information"
      | _ ->
      match symbol with
      | None ->
        return Error "No documentation information"
      | Some symbol ->
        match DocumentationFormatter.getTooltipDetailsFromSymbolUse symbol with
        | None ->
          return Error "No documentation information"
        | Some (signature, footer, cn) ->
            match symbol with
            | SymbolUse.TypeAbbreviation symbol ->
              return Ok (None, Some (symbol.GetAbbriviatedParent().XmlDocSig, symbol.GetAbbriviatedParent().Assembly.FileName |> Option.defaultValue ""), signature, footer, cn)
            | _ ->
              return Ok (Some tip, None, signature, footer, cn)
  }

  member x.TryGetFormattedDocumentationForSymbol (xmlSig: string) (assembly: string) = async {
    let entities = x.GetAllEntities false
    let ent =
      entities |> List.tryFind (fun e ->
        let check = (e.Symbol.XmlDocSig = xmlSig && e.Symbol.Assembly.SimpleName = assembly)
        if not check then
          match e.Symbol with
          | FSharpEntity (_, abrvEnt, _) ->
            abrvEnt.XmlDocSig = xmlSig && abrvEnt.Assembly.SimpleName = assembly
          | _ -> false
        else
          true
      )
    let ent =
      match ent with
      | Some ent -> Some ent
      | None ->
        entities |> List.tryFind (fun e ->
          let check = (e.Symbol.XmlDocSig = xmlSig)
          if not check then
            match e.Symbol with
            | FSharpEntity (_, abrvEnt, _) ->
              abrvEnt.XmlDocSig = xmlSig
            | _ -> false
          else
            true
        )

    let symbol =
      match ent with
      | Some ent -> Some ent.Symbol
      | None ->
        entities |> List.tryPick (fun e ->
          match e.Symbol with
          | FSharpEntity (ent, _, _) ->
            match ent.MembersFunctionsAndValues |> Seq.tryFind (fun f -> f.XmlDocSig = xmlSig) with
            | Some e -> Some (e :> FSharpSymbol)
            | None ->
              match  ent.FSharpFields |> Seq.tryFind (fun f -> f.XmlDocSig = xmlSig) with
              | Some e -> Some (e :> FSharpSymbol)
              | None -> None
          | _ ->
            None
        )

    match symbol with
    | None -> return Error "No matching symbol information"
    | Some symbol ->
        match DocumentationFormatter.getTooltipDetailsFromSymbol symbol with
        | None ->
            return Error "No tooltip information"
        | Some (signature, footer, cn) ->
            match symbol.XmlDoc with
            | FSharpXmlDoc.FromXmlText xmlDoc ->
                let x = xmlDoc.UnprocessedLines
                return Ok (symbol.XmlDocSig, symbol.Assembly.FileName |> Option.defaultValue "", xmlDoc.UnprocessedLines |> Seq.toList , signature, footer, cn)
            | _ -> return Error "No tooltip information"
  }

  member __.TryGetSymbolUse (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None ->
          return (ResultOrString.Error "No ident at this location")
        | Some((_, colu), identIsland) ->

        let identIsland = Array.toList identIsland
        let symboluse = checkResults.GetSymbolUseAtLocation(pos.Line, colu, lineStr, identIsland)
        match symboluse with
        | None ->
          return (ResultOrString.Error "No symbol information found")
        | Some symboluse ->

        let symboluses = checkResults.GetUsesOfSymbolInFile symboluse.Symbol
        return Ok (symboluse, symboluses) }

  member __.TryGetSignatureData (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None ->
          return (ResultOrString.Error "No ident at this location")
        | Some((_, colu), identIsland) ->

        let identIsland = Array.toList identIsland
        let symboluse = checkResults.GetSymbolUseAtLocation(pos.Line, colu, lineStr, identIsland)
        match symboluse with
        | None ->
          return (ResultOrString.Error "No symbol information found")
        | Some symboluse ->
          let fsym = symboluse.Symbol
          match fsym with
          | :? FSharpMemberOrFunctionOrValue as symbol ->
            let typ = symbol.ReturnParameter.Type.Format symboluse.DisplayContext
            if symbol.IsPropertyGetterMethod then
                return Ok(typ, [], [])
            else
              let parms =
                symbol.CurriedParameterGroups
                |> Seq.map (Seq.map (fun p -> p.DisplayName, p.Type.Format symboluse.DisplayContext) >> Seq.toList )
                |> Seq.toList
              let generics =
                symbol.GenericParameters
                |> Seq.map (fun generic ->
                    generic.Name
                )
                |> Seq.toList
              // Abstract members and abstract member overrides with one () parameter seem have a list with an empty list
              // as parameters.
              match parms with
              | [ [] ] when symbol.IsMember && (not symbol.IsPropertyGetterMethod) ->
                return Ok(typ, [ [ ("unit", "unit") ] ], [])
              | _ ->
                return Ok(typ, parms, generics)
          | _ ->
            return (ResultOrString.Error "Not a member, function or value" )
    }

  member __.TryGetF1Help (pos: pos) (lineStr: LineStr) =
    async {
        match Lexer.findLongIdents(pos.Column - 1, lineStr) with
        | None -> return (ResultOrString.Error "No ident at this location")
        | Some((_, colu), identIsland) ->

        let identIsland = Array.toList identIsland
        let help = checkResults.GetF1Keyword(pos.Line, colu, lineStr, identIsland)
        match help with
        | None -> return (ResultOrString.Error "No symbol information found")
        | Some hlp -> return Ok hlp}

  member __.TryGetCompletions (pos: pos) (lineStr: LineStr) filter (getAllSymbols : unit -> AssemblySymbol list) = async {
    try
      let longName = QuickParse.GetPartialLongNameEx(lineStr, pos.Column - 2)
      let residue = longName.PartialIdent

      let getAllSymbols() =
        getAllSymbols()
        |> List.filter (fun entity -> entity.FullName.Contains "." && not (PrettyNaming.IsOperatorDisplayName entity.Symbol.DisplayName))

      let token = Lexer.getSymbol pos.Line (pos.Column - 1) lineStr SymbolLookupKind.Simple [||]
      let isEmpty = longName.QualifyingIdents.IsEmpty && String.IsNullOrWhiteSpace longName.PartialIdent && longName.LastDotPos.IsNone

      match token with
      | Some k when k.Kind = Other && not isEmpty -> return None
      | Some k when k.Kind = Operator  -> return None
      | Some k when k.Kind = Keyword  -> return None
      | _ ->

      let results = checkResults.GetDeclarationListInfo(Some parseResults, pos.Line, lineStr, longName, getAllSymbols)

      let getKindPriority = function
        | CompletionItemKind .CustomOperation -> -1
        | CompletionItemKind .Property -> 0
        | CompletionItemKind .Field -> 1
        | CompletionItemKind .Method (isExtension = false) -> 2
        | CompletionItemKind .Event -> 3
        | CompletionItemKind .Argument -> 4
        | CompletionItemKind .Other -> 5
        | CompletionItemKind .Method (isExtension = true) -> 6

      let decls =
        match filter with
        | Some "StartsWith" ->
          results.Items
          |> Array.filter (fun d -> d.Name.StartsWith(residue, StringComparison.InvariantCultureIgnoreCase))
        | Some "Contains" ->
          results.Items
          |> Array.filter (fun d -> d.Name.IndexOf(residue, StringComparison.InvariantCultureIgnoreCase) >= 0)
        | _ -> results.Items

      let sortedDecls =
          decls
          |> Array.sortWith (fun x y ->
              let mutable n = (not x.IsResolved).CompareTo(not y.IsResolved)
              if n <> 0 then n else
                  n <- (getKindPriority x.Kind).CompareTo(getKindPriority y.Kind)
                  if n <> 0 then n else
                      n <- (not x.IsOwnMember).CompareTo(not y.IsOwnMember)
                      if n <> 0 then n else
                          n <- StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name)
                          if n <> 0 then n else
                            x.MinorPriority.CompareTo(y.MinorPriority))

      let shouldKeywords = sortedDecls.Length > 0 && not results.IsForType && not results.IsError && List.isEmpty longName.QualifyingIdents
      return Some (sortedDecls, residue, shouldKeywords)
    with :? TimeoutException -> return None
  }

  member __.GetAllEntities (publicOnly: bool) : AssemblySymbol list =
      try
        let res = [
          yield! AssemblyContent.GetAssemblySignatureContent AssemblyContentType.Full checkResults.PartialAssemblySignature
          let ctx = checkResults.ProjectContext
          let assembliesByFileName =
            ctx.GetReferencedAssemblies()
            |> List.groupBy (fun asm -> asm.FileName)
            |> List.rev // if mscorlib.dll is the first then FSC raises exception when we try to
                        // get Content.Entities from it.

          for fileName, signatures in assembliesByFileName do
            let contentType = if publicOnly then AssemblyContentType.Public else AssemblyContentType.Full
            let content = AssemblyContent.GetAssemblyContent entityCache.Locking contentType fileName signatures
            yield! content
        ]
        res
      with
      | _ -> []

  member __.GetAllSymbolUsesInFile () = checkResults.GetAllUsesOfAllSymbolsInFile()

  member __.GetSemanticClassification = checkResults.GetSemanticClassification None
  member __.GetAST = parseResults.ParseTree
  member __.GetCheckResults = checkResults
  member __.GetParseResults = parseResults
  member __.FileName: string = parseResults.FileName
