namespace Behide.OnlineServices.Serialization

open System
open Behide.OnlineServices.Serialization
open Microsoft.FSharp.Reflection

type FSharpTypeSerializationProvider() =
    let CreateInstance (objType: Type) =
        Activator.CreateInstance(objType)

    let AsBsonSerializer (value: obj) =
        value :?> MongoDB.Bson.Serialization.IBsonSerializer

    let IsSingleUnionType (objType: Type) =
        FSharpType.IsUnion objType && FSharpType.GetUnionCases objType |> Array.length = 1

    interface MongoDB.Bson.Serialization.IBsonSerializationProvider with
        member _.GetSerializer(objType) =
            if objType = typeof<Guid> then
                Serializers.GuidSerializer()
            elif IsSingleUnionType objType then
                typedefof<Serializers.SingleUnionTypeSerializer<_>>.MakeGenericType(objType)
                |> CreateInstance
                |> AsBsonSerializer
            elif FSharpType.IsUnion objType then
                typedefof<Serializers.DiscriminatedUnionSerializer<_>>.MakeGenericType(objType)
                |> CreateInstance
                |> AsBsonSerializer
            else
                null

module SerializationProviderModule =
    let mutable private isRegistered = false

    let Register() =
        if not isRegistered then
            isRegistered <- true
            FSharpTypeSerializationProvider()
            |> MongoDB.Bson.Serialization.BsonSerializer.RegisterSerializationProvider