namespace DocBook.SharedKernel;

public enum ActorRole
{
    Patient = 1,
    Staff = 2,
}

// Built from the validated token at the edge, so no handler takes an identity from a request body.
public readonly record struct Actor(Guid Id, ActorRole Role)
{
    public bool IsStaff => Role == ActorRole.Staff;

    public bool Is(Guid patientId) => Role == ActorRole.Patient && Id == patientId;
}
