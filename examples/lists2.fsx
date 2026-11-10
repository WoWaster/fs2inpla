let rec range a b =
    if a > b then [] else a :: range (a + 1) b

let rec filter p xs =
    match xs with
    | [] -> []
    | h :: t -> if p h then h :: filter p t else filter p t

let rec map f xs =
    match xs with
    | [] -> []
    | h :: t -> f h :: map f t

let rec append xs ys =
    match xs with
    | [] -> ys
    | h :: t -> h :: append t ys

let rec length xs =
    match xs with
    | [] -> 0
    | _ :: t -> 1 + length t

let rev xs =
    let rec go acc xs =
        match xs with
        | [] -> acc
        | h :: t -> go (h :: acc) t

    go [] xs

let evens = filter (fun x -> x % 2 = 0) (range 1 10)
let k = 3
let main = append (rev evens) (map (fun x -> x + k) [ length evens; 2 ])
