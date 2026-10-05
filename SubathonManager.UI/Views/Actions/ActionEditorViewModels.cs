using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.UI.Views.Actions;

public abstract class ActionEditorVm : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ActionNodeVm : ActionEditorVm {
    public ActionNodeVm(ActionNode node) {
        Node = node;
        Input = new ActionConnectorVm(this, true);
        Output = new ActionConnectorVm(this, false);
        ElseOutput = new ActionConnectorVm(this, false, ActionEdge.ElsePort);
    }

    public ActionNode Node { get; }
    public ActionConnectorVm Input { get; }
    public ActionConnectorVm Output { get; }

    public ActionConnectorVm ElseOutput { get; }

    public bool IsCondition => Node.Step.Type == ActionStepType.Condition;
    public bool IgnoresErrors => Node.IgnoreErrors;

    public Point Location {
        get => new(Node.X, Node.Y);
        set {
            if (Math.Abs(value.X - Node.X) < 0.1 && Math.Abs(value.Y - Node.Y) < 0.1) return;
            Node.X = value.X;
            Node.Y = value.Y;
            Raise();
        }
    }

    public string Title => $"{Node.Step.Type.GetGroup()} - {Node.Step.Type.GetLabel()}";
    public string Summary => Node.Step.Describe();
    public bool IsStart { get; private set; }
    public bool IsEnd { get; private set; }

    public int Branches { get; private set; }
    public bool HasBranches => Branches > 1;
    public string BranchLabel => $"{Branches}b";

    public string? Error { get; private set; }
    public bool HasError => Error != null;

    public bool IsDisabled => Node.Disabled;
    public double CardOpacity => Node.Disabled ? 0.45 : 1;

    public void Refresh(ActionGraph graph) {
        Raise(nameof(IsDisabled));
        Raise(nameof(CardOpacity));
        Raise(nameof(IgnoresErrors));
        IsStart = !graph.Incoming(Node.Id).Any();

        List<ActionEdge> outgoing = graph.Edges.Where(e => e.From == Node.Id).ToList();
        Branches = outgoing.GroupBy(e => e.Port).Select(g => g.Count()).DefaultIfEmpty(0).Max();
        IsEnd = outgoing.Count == 0;
        Error = Node.Step.IsValid(out string error) ? null : error;

        Raise(nameof(Title));
        Raise(nameof(Summary));
        Raise(nameof(IsStart));
        Raise(nameof(IsEnd));
        Raise(nameof(Branches));
        Raise(nameof(HasBranches));
        Raise(nameof(BranchLabel));
        Raise(nameof(Error));
        Raise(nameof(HasError));
    }
}

public sealed class ActionConnectorVm(ActionNodeVm owner, bool isInput, string? port = null) : ActionEditorVm {
    private Point _anchor;

    public ActionNodeVm Owner { get; } = owner;
    public bool IsInput { get; } = isInput;
    public string? Port { get; } = port;

    public Point Anchor {
        get => _anchor;
        set {
            if (_anchor == value) return;
            _anchor = value;
            Raise();
        }
    }
}

public sealed record ActionConnectionVm(ActionEdge Edge, ActionConnectorVm Source, ActionConnectorVm Target) {
    public bool IsElse => Edge.Port == ActionEdge.ElsePort;
}

internal sealed class EditorCommand(Action<object?> execute) : ICommand {
    public event EventHandler? CanExecuteChanged {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) {
        return true;
    }

    public void Execute(object? parameter) {
        execute(parameter);
    }
}