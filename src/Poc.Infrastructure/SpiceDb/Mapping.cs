using Poc.Application.Authorization;
using Az = Authzed.Api.V1;

namespace Poc.Infrastructure.SpiceDb;

internal static class Mapping
{
    public static Az.ObjectReference ToProto(this ResourceRef r)
    {
        Require(r.Type, "resource type");
        Require(r.Id, "resource id");
        return new Az.ObjectReference { ObjectType = r.Type, ObjectId = r.Id };
    }

    public static Az.SubjectReference ToProto(this SubjectRef s)
    {
        Require(s.Type, "subject type");
        Require(s.Id, "subject id");
        var subject = new Az.SubjectReference { Object = new Az.ObjectReference { ObjectType = s.Type, ObjectId = s.Id } };
        if (!string.IsNullOrEmpty(s.Relation)) subject.OptionalRelation = s.Relation;
        return subject;
    }

    public static Az.Relationship ToProto(this Relationship r)
    {
        Require(r.Relation, "relation");
        return new Az.Relationship { Resource = r.Resource.ToProto(), Relation = r.Relation, Subject = r.Subject.ToProto() };
    }

    public static Az.RelationshipUpdate ToProto(this RelationshipUpdate u) => new()
    {
        Operation = u.Operation switch
        {
            RelationshipOperation.Touch => Az.RelationshipUpdate.Types.Operation.Touch,
            RelationshipOperation.Delete => Az.RelationshipUpdate.Types.Operation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(u), u.Operation, "Unknown relationship operation."),
        },
        Relationship = u.Relationship.ToProto(),
    };

    public static Az.RelationshipFilter ToProto(this RelationshipFilter f)
    {
        var filter = new Az.RelationshipFilter { ResourceType = f.ResourceType };
        if (f.ResourceId is not null) filter.OptionalResourceId = f.ResourceId;
        if (f.Relation is not null) filter.OptionalRelation = f.Relation;
        if (f.Subject is { } s)
        {
            var sf = new Az.SubjectFilter { SubjectType = s.SubjectType };
            if (s.SubjectId is not null) sf.OptionalSubjectId = s.SubjectId;
            if (s.SubjectRelation is not null)
                sf.OptionalRelation = new Az.SubjectFilter.Types.RelationFilter { Relation = s.SubjectRelation };
            filter.OptionalSubjectFilter = sf;
        }

        return filter;
    }

    public static Relationship FromProto(this Az.Relationship r) => new(
        new ResourceRef(r.Resource.ObjectType, r.Resource.ObjectId),
        r.Relation,
        new SubjectRef(r.Subject.Object.ObjectType, r.Subject.Object.ObjectId,
            string.IsNullOrEmpty(r.Subject.OptionalRelation) ? null : r.Subject.OptionalRelation));

    private static void Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"The {what} must not be empty.");
    }
}
