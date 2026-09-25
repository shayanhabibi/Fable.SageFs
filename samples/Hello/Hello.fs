module Hello

type Shape =
    | Circle of radius: float
    | Rect of width: float * height: float

let area shape =
    match shape with
    | Circle r -> System.Math.PI * r * r
    | Rect (w, h) -> w * h

let greet (name: string) = $"Hello, {name}!"

let total = [ Circle 1.0; Rect(2.0, 3.0) ] |> List.sumBy area
