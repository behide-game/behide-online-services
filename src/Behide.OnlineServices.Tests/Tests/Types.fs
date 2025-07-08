module Behide.OnlineServices.Tests.Types

open Behide.OnlineServices
open Expecto

[<FTests>]
let tests =
    testList "Types" [
        testList "Pair" [
            testCase "Test pairs are unordered" (fun () ->
                let stringPair1 = Pair.create "a" "b"
                let stringPair2 = Pair.create "b" "a"

                let intPair1 = Pair.create 69 42
                let intPair2 = Pair.create 42 69

                Expect.equal stringPair1 stringPair2 "Pairs should equal"
                Expect.equal intPair1 intPair2 "Pairs should equal"
            )

            testCase "Test isInPair works" (fun () ->
                let stringPair = Pair.create "a" "b"
                Expect.isTrue ("a" |> Pair.isInPair stringPair) "Pair should contain \"a\""
                Expect.isTrue ("b" |> Pair.isInPair stringPair) "Pair should contain \"b\""

                let intPair = Pair.create 69 42
                Expect.isTrue (69 |> Pair.isInPair intPair) "Pair should contain 69"
                Expect.isTrue (42 |> Pair.isInPair intPair) "Pair should contain 42"
            )
        ]

        testList "Signaling" [
            testList "RoomId" [
                testTheory "correct parsing"
                    [ "abcd"
                      "ABCD"
                      "0123"
                      "a1b3"
                      "A1b3"
                      "A1B3" ]
                    (Signaling.RoomId.tryParse >> Flip.Expect.isSome "Room id should be parsable")

                testTheory "incorrect room id not parsable"
                    [ "abc"
                      "abç"
                      "abçd"
                      "ABÇD"
                      "^123"
                      "ä1b3"
                      "A1ü3"
                      "Ü1Ö3"
                      "01234" ]
                    (Signaling.RoomId.tryParse >> Flip.Expect.isNone "Room id should not be parsable")
            ]
        ]
    ]
