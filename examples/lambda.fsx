// Well-typed combinators and Church numerals.
let id x = x
let k x y = x
let compose f g x = f (g x)
let two f x = f (f x)
let three f x = f (f (f x))
let add m n f x = m f (n f x)
let toInt n = n (fun x -> x + 1) 0
let main = toInt (add two three) + k (id 0) 1 + compose (fun x -> x * 10) id 1
