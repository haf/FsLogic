module MiniKanren.Goal

open MiniKanren.Substitution
open MiniKanren.Stream
open System
open Microsoft.FSharp.Quotations

//A goal is a function that maps a substitution to an
//ordered sequence of zero or more values.
type Goal = Subst -> Stream<Subst>

let equiv (u:'a) (v:'a) : Goal =
    fun a -> 
        unify u v a
        |> Option.map result
        |> Option.defaultTo mzero

//this trick for unification overloading doesn't
//reallly work well in all cases.
type Equiv = Equiv with
    static member (?<-)(Equiv, l, v) = 
        equiv l <@ v @>
    static member (?<-)(Equiv, v, r) = 
        equiv <@ v @> r
    static member (?<-)(Equiv, l:Quotations.Expr<'a>,r:Quotations.Expr<'a>) =
        equiv l r
 
let inline (-=-) l r = Equiv ? (l) <- r

let all ((g::gs) as goals:list<Goal>) : Goal =
    fun a -> delay (fun () -> (bindMany (g a) gs))

let conde (goals:#seq<list<Goal>>) : Goal =
    fun a -> 
        delay (fun () -> (mplusMany (goals |> Seq.map (fun (g::gs) -> bindMany (g a) gs) |> Seq.toList)))

let conda (goals:list<list<Goal>>) : Goal = 
    let rec ifa subst = function
        | [] | [[]] -> mzero
        | (g0::g)::gs ->
            let rec loop = function
                | MZero -> ifa subst gs
                | Inc f -> loop f.Value
                | Unit _  
                | Choice (_,_) as a-> bindMany a g
            loop (g0 subst)
    fun subst -> ifa subst (goals |> Seq.toList)

let condu (goals:list<list<Goal>>) : Goal = 
    let rec ifu subst = function
        | [] | [[]] -> mzero
        | (g0::g)::gs ->
            let rec loop = function
                | MZero -> ifu subst gs
                | Inc f -> loop f.Value
                | Unit _ as a -> bindMany a g
                | Choice (a,_) -> bindMany (result a) g
            loop (g0 subst)
    fun subst -> ifu subst (goals |> Seq.toList)

let (|||) g1 g2 =
    fun a -> mplus (g1 a) (g2 a)

let (&&&) g1 g2 = 
    fun a -> bind (g1 a) g2

let inline recurse fg =
    fun a -> delay (fun () -> fg() a)

let rec fix f x = fun a -> delay (fun () -> (f (fix f) x a))

let rec take n f =
    if n = 0 then 
        []
    else
        match f with
        | MZero -> []
        | Inc (Lazy s) -> take n s
        | Unit a -> [a]
        | Choice(a,f) ->  a :: take (n-1) f

let inline run n (f: _ -> Goal) =
    delay (fun () -> let x = fresh()
                     bind (f x Map.empty) (reify x >> result))
    |> take n

let inline runEval n (f: _ -> Goal) =
    run n f
    |> List.map Swensen.Unquote.Operators.evalRaw

let inline runShow n (f: _ -> Goal) =
    run n f
    |> Seq.map Swensen.Unquote.Operators.decompile
    |> String.concat Environment.NewLine

//impure operators
let project (v:Expr<'a>) (f:'a -> Goal) : Goal =
    fun s -> 
        //assume atom here..otherwise fail
        let x = walkMany v s
        f (Swensen.Unquote.Operators.evalRaw x) s

let copyTerm u v : Goal =
    let rec buildSubst u s : Subst=
        match u with
        | LVar (Find s _) -> s
        | LVar u -> Map.add u (upcast fresh()) s
        | Patterns.NewUnionCase (_, exprs)
        | Patterns.NewTuple exprs -> 
            List.fold (fun s expr -> buildSubst expr s) s exprs
        | _ -> s
    fun s ->
        let u = walkMany u s
        equiv (walkMany u (buildSubst u Map.empty)) v s

