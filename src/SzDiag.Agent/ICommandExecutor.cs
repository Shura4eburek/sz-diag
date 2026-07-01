namespace SzDiag.Agent;

public interface ICommandExecutor
{
    CommandResult Run(string command);
}
