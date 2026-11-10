// A recursive function capturing more variables than an Inpla agent has ports (5).
let main =
    let a = 1
    let b = 2
    let c = 3
    let d = 4
    let e = 5
    let f = 6

    let rec go n =
        if n = 0 then a + b + c + d + e + f else go (n - 1)

    go 3
