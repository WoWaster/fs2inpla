let rec range a b = if a > b then [] else a :: range (a + 1) b
let rec map f xs =
    match xs with
    | [] -> []
    | h :: t -> f h :: map f t
let rec filter p xs =
    match xs with
    | [] -> []
    | h :: t -> if p h then h :: filter p t else filter p t
let rec foldl f acc xs =
    match xs with
    | [] -> acc
    | h :: t -> foldl f (f acc h) t
let sum xs = foldl (fun a b -> a + b) 0 xs
let main = sum (map (fun x -> x * x) (range 1 10))
