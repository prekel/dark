module ApiServer.Ui

open Microsoft.AspNetCore.Http
open Giraffe
open System.Text

open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharpPlus
open Prelude
open Tablecloth

module File = LibBackend.File
module Config = LibBackend.Config
module Session = LibBackend.Session
module Account = LibBackend.Account

let adminUiTemplate : Lazy<string> =
  lazy
    (let liveReloadStr =
      "<script type=\"text/javascript\" src=\"//localhost:35729/livereload.js\"> </script>"

     let liveReloadJs = if Config.browserReloadEnabled then liveReloadStr else ""
     let uiHtml = File.readfile Config.Templates "ui.html"
     let appSupport = File.readfile Config.Webroot "appsupport.js"

     // Load as much of this as we can in advance
     // CLEANUP make APIs to load the dynamic data
     uiHtml
       .Replace("{{ENVIRONMENT_NAME}}", LibService.Config.envDisplayName)
       .Replace("{{LIVERELOADJS}}", liveReloadJs)
       .Replace("{{HEAPIO_ID}}", Config.heapioId)
       .Replace("{{ROLLBARCONFIG}}", Config.rollbarJs)
       .Replace("{{PUSHERCONFIG}}", LibBackend.Pusher.jsConfigString)
       .Replace("{{USER_CONTENT_HOST}}", Config.bwdServerContentHost)
       .Replace("{{APPSUPPORT}}", appSupport)
       .Replace("{{BUILD_HASH}}", LibService.Config.buildHash))

let hashedFilename (filename : string) (hash : string) : string =
  match filename.Split '.' with
  | [| name; extension |] -> $"/{name}-{hash}{extension}"
  | _ -> failwith "incorrect hash name"



let prodHashReplacements : Lazy<Map<string, string>> =
  lazy
    ("etags.json"
     |> File.readfile Config.Webroot
     |> Json.Vanilla.deserialize<Map<string, string>>
     |> Map.remove "__date"
     |> Map.remove ".gitkeep"
     // Only hash our assets, not vendored assets
     |> Map.filterWithIndex
          (fun k _ ->
            not (String.includes "vendor/" k || String.includes "blazor/" k))
     |> Map.toList
     |> List.map
          (fun (filename, hash) -> ($"/{filename}", hashedFilename filename hash))
     |> Map.ofList)

let prodHashReplacementsString : Lazy<string> =
  lazy (prodHashReplacements.Force() |> Json.Vanilla.serialize)


// FSTODO: clickjacking/ CSP/ frame-ancestors
let uiHtml
  (canvasID : CanvasID)
  (canvasName : CanvasName.T)
  (csrfToken : string)
  (localhostAssets : string option)
  (accountCreated : System.DateTime)
  (user : Account.UserInfo)
  : string =

  let shouldHash =
    if localhostAssets = None then Config.hashStaticFilenames else false

  let hashReplacements =

    if shouldHash then prodHashReplacementsString.Force() else "{}"

  let accountCreatedMsTs =
    System.DateTimeOffset(accountCreated).ToUnixTimeMilliseconds()
    // CLEANUP strip milliseconds to make it identical to ocaml
    |> fun x -> (x / 1000L) * 1000L
    |> toString

  let staticHost =
    match localhostAssets with
    // TODO: can add other people to this for easier debugging
    | Some username -> $"darklang-{username}.ngrok.io"
    | _ -> Config.apiServerStaticHost


  // TODO: allow APPSUPPORT in here
  let t = StringBuilder(adminUiTemplate.Force())

  // CLEANUP move functions into an API call, or even to the CDN
  // CLEANUP move the user info into an API call
  t
    .Replace("{{ALLFUNCTIONS}}", (Functions.functions user.admin).Force())
    .Replace("{{STATIC}}", staticHost)
    .Replace("{{USER_CONTENT_HOST}}", Config.bwdServerContentHost)
    .Replace("{{USER_USERNAME}}", user.username.ToString())
    .Replace("{{USER_EMAIL}}", user.email)
    .Replace("{{USER_FULLNAME}}", user.name)
    .Replace("{{USER_CREATED_AT_UNIX_MSTS}}", accountCreatedMsTs)
    .Replace("{{USER_IS_ADMIN}}", (if user.admin then "true" else "false"))
    .Replace("{{USER_ID}}", user.id.ToString())
    .Replace("{{CANVAS_ID}}", (canvasID.ToString()))
    .Replace("{{CANVAS_NAME}}", canvasName.ToString())
    .Replace("{{STATIC}}", staticHost)
    .Replace("{{HASH_REPLACEMENTS}}", hashReplacements)
    .Replace("{{CSRF_TOKEN}}", csrfToken)
  |> ignore<StringBuilder>

  // Replace any filenames in the file with the hashed version
  if shouldHash then
    prodHashReplacements
    |> Lazy.force
    |> Map.iter
         (fun filename hash ->
           t.Replace(filename, hashedFilename filename hash) |> ignore<StringBuilder>)

  t.ToString()

let uiHandler (ctx : HttpContext) : Task<string> =
  task {
    let user = Middleware.loadUserInfo ctx
    let sessionData = Middleware.loadSessionData ctx
    let canvasInfo = Middleware.loadCanvasInfo ctx
    let! createdAt = Account.getUserCreatedAt user.username
    let localhostAssets = ctx.TryGetQueryStringValue "localhost-assets"

    return
      uiHtml
        canvasInfo.id
        canvasInfo.name
        sessionData.csrfToken
        localhostAssets
        createdAt
        user
  }
