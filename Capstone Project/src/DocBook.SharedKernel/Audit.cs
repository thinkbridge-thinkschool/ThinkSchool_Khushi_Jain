namespace DocBook.SharedKernel;

// A port: the use case says what happened, the module's infrastructure decides where it is written.
public interface IAuditTrail
{
    void Record(Actor actor, string action, Guid subjectId);
}
