module Clipboard exposing (..)


-- Dark
import Types exposing (..)
import Toplevel as TL
import Pointer as P
import AST
import Entry
import Blank

copy : Model -> Toplevel -> (Maybe Pointer) -> Modification
copy m tl mp =
  case TL.asHandler tl of
    Nothing -> NoChange
    Just h ->
      case mp of
        Nothing -> CopyToClipboard (Just <| AST.toPD h.ast)
        Just p ->
          let pid = P.toID p
          in CopyToClipboard (AST.subData pid h.ast)

cut : Model -> Toplevel -> Pointer -> Modification
cut m tl p =
  let pid = P.toID p
      pred = TL.getPrevBlank tl (Just p)
  in
    case TL.asHandler tl of
      Nothing -> NoChange
      Just h ->
        let newClipboard = AST.subData pid h.ast
            newAst = AST.deleteExpr p h.ast (gid ())
        in Many [ CopyToClipboard newClipboard
                , RPC ( [ SetHandler tl.id tl.pos { h | ast = newAst } ]
                        , FocusNext tl.id pred)
                ]

paste : Model -> Toplevel -> Pointer -> Modification
paste m tl p =
  case m.clipboard of
    Nothing -> NoChange
    Just pd ->
      let cloned = TL.clonePointerData pd
      in case TL.asHandler tl of
           Nothing -> NoChange
           Just h ->
             let newAst = AST.replace p cloned h.ast
             in RPC ( [ SetHandler tl.id tl.pos { h | ast = newAst } ]
                    , FocusNext tl.id (Just (P.pdToP cloned)))

peek : Model -> Clipboard
peek m =
  Maybe.map TL.clonePointerData m.clipboard

newFromClipboard : Model -> Pos -> Modification
newFromClipboard m pos =
  let nid = gtlid ()
      ast =
        case peek m of
          Nothing -> Blank.new ()
          Just a ->
            case a of
              PExpr exp -> exp
              _ -> Blank.new ()
      spec = Entry.newHandlerSpec ()
      handler = { ast = ast, spec = spec }
  in
      RPC ([SetHandler nid pos handler], FocusNext nid Nothing)


