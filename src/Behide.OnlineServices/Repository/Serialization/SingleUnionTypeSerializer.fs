namespace Behide.OnlineServices.Serialization.Serializers

open Microsoft.FSharp.Reflection
open MongoDB.Bson.IO
open MongoDB.Bson.Serialization
open MongoDB.Bson.Serialization.Serializers

type SingleUnionTypeSerializer<'t>() =
    inherit SerializerBase<'t>()

    let case = FSharpType.GetUnionCases(typeof<'t>, true) |> Array.head

    let deserBy context args t =
        BsonSerializer.LookupSerializer(t).Deserialize(context, args)

    let serBy context args t v =
        BsonSerializer.LookupSerializer(t).Serialize(context, args, v)


    override _.Deserialize(context, args): 't =
        let field = case.GetFields() |> Array.head
        context.Reader.ReadStartDocument()

        context.Reader.ReadName() |> ignore
        let item = BsonSerializer.LookupSerializer(field.PropertyType).Deserialize(context, args)

        context.Reader.ReadEndDocument()
        FSharpValue.MakeUnion(case, [| item |]) :?> 't

    override _.Serialize(context, args, value) =
        let case, fields = FSharpValue.GetUnionFields(value, typeof<'t>)
        let field = case.GetFields() |> Array.head
        let fieldValue = fields |> Array.head

        context.Writer.WriteStartDocument()

        context.Writer.WriteName(case.Name)
        BsonSerializer.LookupSerializer(field.PropertyType).Serialize(context, args, fieldValue)

        context.Writer.WriteEndDocument()