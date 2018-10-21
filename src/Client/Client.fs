module Client

open Elmish
open Elmish.React
open Fable.PowerPack

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

open System
open Fable.Helpers.React
open Fable.Helpers.React.Props
open Fable.PowerPack.Fetch
open Fulma
open Fulma.FontAwesome
open ServerCore.Domain

type Model = {
    Tags: TagList option
    IsUploading :bool
    Message : string
    File: obj option
    Firmware: Firmware option
    ShownTags : Tag []
    FilterText : string
}

type Msg =
| FetchTags
| FileNameChanged of obj
| FilterChanged of string
| Upload
| RefreshList
| FileUploaded of Tag
| UploadFailed of exn
| TagsLoaded of Result<TagList, exn>
| FirmwareLoaded of Result<Firmware, exn>
| Err of exn

let runIn (timeSpan:System.TimeSpan) successMsg errorMsg =
    let p() = promise {
        do! Promise.sleep (int timeSpan.TotalMilliseconds)
        return ()
    }
    Cmd.ofPromise p () (fun _ -> successMsg) errorMsg


let userID = "9bb2b109-bf08-4342-9e09-f4ce3fb01c0f"

let fetchData() = promise {
    let! res = Fetch.fetch (sprintf "api/usertags/%s" userID) []
    let! txt = res.text()

    match Decode.fromString TagList.Decoder txt with
    | Ok tags -> return tags
    | Error msg -> return failwith msg
}

let fetchFirmware() = promise {
    let! res = Fetch.fetch "api/firmware" []
    let! txt = res.text()

    match Decode.fromString Firmware.Decoder txt with
    | Ok tags -> return tags
    | Error msg -> return failwith msg
}


let uploadFile (fileName) = promise {
    let formData = Fable.Import.Browser.FormData.Create()
    formData.append("file",fileName)

    let props =
        [ RequestProperties.Method HttpMethod.POST
          Fetch.requestHeaders [
            // HttpRequestHeaders.Authorization ("Bearer " + model.User.Token)
            //HttpRequestHeaders.ContentType "multipart/form-data"
             ]
          RequestProperties.Body (unbox formData) ]

    let! res = Fetch.fetch "api/upload" props
    let! txt = res.text()

    match Decode.fromString Tag.Decoder txt with
    | Ok tags -> return tags
    | Error msg -> return failwith msg
}

let fetchFirmwareCmd = Cmd.ofPromise fetchFirmware () (Ok >> FirmwareLoaded) (Error >> FirmwareLoaded)
let fetchTagsCmd = Cmd.ofPromise fetchData () (Ok >> TagsLoaded) (Error >> TagsLoaded)

let init () : Model * Cmd<Msg> =
    let initialModel = {
        Tags = None
        Firmware = None
        File = None
        IsUploading = false
        FilterText = ""
        Message = ""
        ShownTags = [||]
    }

    initialModel,
        Cmd.batch [
            Cmd.ofMsg FetchTags
            fetchFirmwareCmd
        ]


let update (msg : Msg) (model : Model) : Model * Cmd<Msg> =
    match msg with

    | FetchTags ->
        model, fetchTagsCmd

    | FilterChanged txt ->
        { model with FilterText = txt }, Cmd.ofMsg RefreshList

    | RefreshList ->
        let tags =
            match model.Tags with
            | None -> [||]
            | Some tagList ->
                if String.IsNullOrWhiteSpace model.FilterText then
                    tagList.Tags
                else
                    let s = model.FilterText.ToLower()
                    tagList.Tags
                    |> Array.filter (fun x -> x.Description.ToLower().Contains(s) || x.Object.ToLower().Contains(s) || x.Token.Contains(s))

        { model with ShownTags = tags }, Cmd.none

    | FirmwareLoaded (Ok tags) ->
        { model with Firmware = Some tags }, Cmd.none

    | FirmwareLoaded _ ->
        { model with Firmware = None }, Cmd.none

    | TagsLoaded (Ok tags) ->
        { model with Tags = Some tags }, Cmd.ofMsg RefreshList

    | TagsLoaded _  ->
        model, Cmd.ofMsg FetchTags

    | FileNameChanged file ->
        { model with File = Some file }, Cmd.none

    | FileUploaded tag ->
        { model with IsUploading = false; Message = "Done" }, Cmd.ofMsg FetchTags

    | UploadFailed exn ->
        { model with IsUploading = false; Message = exn.Message }, Cmd.ofMsg FetchTags

    | Upload ->
        match model.File with
        | None -> model, Cmd.none
        | Some fileName ->
            { model with File = None; IsUploading = true; Message = "Upload started" }, Cmd.ofPromise uploadFile fileName FileUploaded UploadFailed

    | Err exn ->
        { model with Message = exn.Message }, Cmd.none //runIn (System.TimeSpan.FromSeconds 5.) Fetch Err



open Fable.Helpers.React
open Fable.Helpers.React.Props
open Fable.Core.JsInterop



let audioHubComponents =
    let components =
        span [ ]
           [
             a [ Href "https://safe-stack.github.io/docs" ] [ str "SAFE-Stack" ]
             str ", "
             a [ Href "https://www.raspberrypi.org/" ] [ str "Raspberry Pi" ]
             str " and a lot of fun."
           ]

    p [ ]
        [ str " powered by: "
          components ]

let navBrand =
    Navbar.Brand.div [ ]
        [ Navbar.Item.a
            [ Navbar.Item.Props
                [ Href "https://safe-stack.github.io/"
                  Style [ BackgroundColor "#00d1b2" ] ] ]
            [ img [ Src "https://safe-stack.github.io/images/safe_top.png"
                    Alt "Logo" ] ]
          Navbar.burger [ ]
            [ span [ ] [ ]
              span [ ] [ ]
              span [ ] [ ] ] ]

let navMenu =
    Navbar.menu [ ]
        [ Navbar.End.div [ ]
            [ Navbar.Item.a [ ]
                [ str "Home" ]
              Navbar.Item.a [ ]
                [ str "Examples" ]
              Navbar.Item.a [ Navbar.Item.Option.Props [Href "https://github.com/forki/Audio#installation"]]
                [ str "Documentation" ]
              Navbar.Item.div [ ]
                [ Button.a
                    [ Button.Size IsSmall
                      Button.Props [ Href "https://github.com/forki/Audio" ] ]
                    [ Icon.faIcon [ ]
                        [ Fa.icon Fa.I.Github; Fa.fw ]
                      span [ ] [ str "View Source" ] ] ] ] ]

let filterBox (model : Model) (dispatch : Msg -> unit) =
    Box.box' [ ]
        [ Field.div [ Field.IsGrouped ]
            [ Control.p [ Control.IsExpanded ]
                [ Input.text
                    [ Input.OnChange (fun x -> dispatch (FilterChanged x.Value))
                      Input.Value model.FilterText ] ]
              Control.p [ ]
                [ Button.a
                    [ Button.Color IsPrimary
                      Button.OnClick (fun _ -> dispatch FetchTags) ]
                    [ str "Search" ] ] ] ]

let tagsTable (model : Model) (dispatch : Msg -> unit) =
    match model.Tags with
    | None ->
        div [] []
    | Some _ ->
        div [] [
            filterBox model dispatch
            table [][
                thead [][
                    tr [] [
                        th [] [ str "Object"]
                        th [] [ str "Description"]
                    ]
                ]
                tbody [][
                    for tag in model.ShownTags ->
                        tr [ Id tag.Token ] [
                            yield td [ Title tag.Token ] [ str tag.Object ]
                            match tag.Action with
                            | TagAction.PlayMusik url -> yield td [ ] [ a [Href url ] [str tag.Description ] ]
                            | TagAction.PlayYoutube url -> yield td [ ] [ a [Href url ] [str tag.Description ] ]
                            | _ -> yield td [ Title (sprintf "%O" tag.Action) ] [ str tag.Description ]
                        ]
                ]
            ]
        ]

let view (model : Model) (dispatch : Msg -> unit) =
    Hero.hero
        [ Hero.IsFullHeight
          Hero.IsBold ]
        [ Hero.head [ ]
            [ Navbar.navbar [  ]
                [ Container.container [ ]
                    [ navBrand
                      navMenu ] ] ]
          Hero.body [ ]
            [ Container.container
                [ Container.Modifiers [ Modifier.TextAlignment (Screen.All, TextAlignment.Centered) ] ]
                [ Columns.columns [ Columns.IsVCentered ]
                    [ Column.column
                        [ Column.Width (Screen.All, Column.Is5) ]
                        [ 
                          div [] [
                            input [
                                Type "file"
                                OnChange (fun x -> FileNameChanged (!!x.target?files?(0)) |> dispatch) ]
                            br []
                            button [ OnClick (fun _ -> dispatch Upload) ] [str "Upload"]
                            br []
                            str "Message: "
                            str model.Message]
                        ]
                      Column.column
                       [ Column.Width (Screen.All, Column.Is5)
                         Column.Offset (Screen.All, Column.Is1) ]
                       [ Heading.h1 [ Heading.Is2 ]
                           [ match model.Firmware with
                             | None -> yield str "Audio Hub"
                             | Some fw -> yield a [ Href fw.Url ] [ str ("Audio Hub " + fw.Version) ] ]
                         Heading.h2
                           [ Heading.IsSubtitle
                             Heading.Is4 ]
                           [ audioHubComponents ]
                         tagsTable model dispatch ] ] ] ]
          Hero.foot [ ]
            [ Container.container [ ]
                [ Tabs.tabs [ Tabs.IsCentered ]
                    [ ul [ ]
                        [ li [ ]
                            [ a [ ]
                                [ str "(c) Steffen Forkmann 2018" ] ] ] ] ] ] ]



#if DEBUG
open Elmish.Debug
open Elmish.HMR
#endif

Program.mkProgram init update view
#if DEBUG
|> Program.withConsoleTrace
|> Program.withHMR
#endif
|> Program.withReact "elmish-app"
#if DEBUG
|> Program.withDebugger
#endif
|> Program.run
