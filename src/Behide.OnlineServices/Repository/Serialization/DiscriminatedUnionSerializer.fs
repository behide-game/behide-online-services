namespace Behide.OnlineServices.Serialization.Serializers

open Microsoft.FSharp.Reflection
open MongoDB.Bson.IO
open MongoDB.Bson.Serialization
open MongoDB.Bson.Serialization.Serializers

type DiscriminatedUnionSerializer<'t>() =
    inherit SerializerBase<'t>()
    let CaseNameField = "Case"
    let ValueFieldName = "Fields"
    let Cases =
        typeof<'t>
        |> FSharpType.GetUnionCases
        |> Seq.map(fun x -> (x.Name, x))
        |> dict

    let deserBy context args t =
        BsonSerializer.LookupSerializer(t).Deserialize(context, args)

    let serBy context args t v =
        BsonSerializer.LookupSerializer(t).Serialize(context, args, v)

    let ReadItems context args types =
        types
        |> Seq.fold(fun state t -> (deserBy context args t) :: state) []
        |> Seq.toArray
        |> Array.rev

    override _.Deserialize(context, args): 't =
        context.Reader.ReadStartDocument()
        let name = context.Reader.ReadString(CaseNameField)
        let union = Cases.[name]

        context.Reader.ReadName(ValueFieldName)
        context.Reader.ReadStartArray()

        let items = ReadItems context args (union.GetFields() |> Seq.map(fun f -> f.PropertyType))

        context.Reader.ReadEndArray()
        context.Reader.ReadEndDocument()

        FSharpValue.MakeUnion(union, items) :?> 't

    override _.Serialize(context, args, value) =
        context.Writer.WriteStartDocument()
        let case, fields = FSharpValue.GetUnionFields(value, typeof<'t>)

        context.Writer.WriteString(CaseNameField, case.Name)
        context.Writer.WriteStartArray(ValueFieldName)

        fields
        |> Seq.zip(case.GetFields())
        |> Seq.iter(fun (field, value) -> serBy context args field.PropertyType value)

        context.Writer.WriteEndArray()
        context.Writer.WriteEndDocument()