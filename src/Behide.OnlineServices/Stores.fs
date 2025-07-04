module Behide.OnlineServices.Store

open System.Collections.Concurrent
open FsToolkit.ErrorHandling

type IStore<'Id, 'Item> =
    abstract Add: 'Id -> 'Item -> bool
    abstract Get: 'Id -> 'Item option
    abstract GetByPredicate: ('Item -> bool) -> ('Id * 'Item) option
    abstract Remove: 'Id -> bool
    abstract Update: 'Id -> oldValue: 'Item -> newValue: 'Item -> bool

type Store<'Id, 'Item when 'Id: not null>() =
    let dict = new ConcurrentDictionary<'Id, 'Item>()

    interface IStore<'Id, 'Item> with
        member _.Add id item = dict.TryAdd(id, item)
        member _.Remove id = dict.TryRemove(id) |> fst
        member _.Get id = dict.TryGetValue(id) |> Option.ofPair

        member _.GetByPredicate predicate =
            dict
            |> Seq.tryFind (_.Value >> predicate)
            |> Option.map _.Deconstruct()

        /// Don't forget to use "lock"
        member _.Update id oldValue newValue =
            dict.TryUpdate(id, newValue, oldValue)
