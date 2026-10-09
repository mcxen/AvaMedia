using Avalonia;
using Avalonia.Controls;

namespace AvaMedia.Desktop.Controls;

/// <summary>Shares an existing numeric field's value, including logarithmic large ranges.</summary>
public sealed class ParameterSlider : Slider
{
    public static readonly StyledProperty<NumericUpDown?> InputProperty=AvaloniaProperty.Register<ParameterSlider,NumericUpDown?>(nameof(Input));
    public NumericUpDown? Input{get=>GetValue(InputProperty);set=>SetValue(InputProperty,value);}
    private bool _syncing;
    private double _minimum,_maximum,_step;
    private bool _logarithmic;
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==InputProperty)
        {
            if(change.OldValue is NumericUpDown old)old.PropertyChanged-=InputChanged;
            if(Input is not null)Input.PropertyChanged+=InputChanged;
            Refresh();
        }
        else if(change.Property==ValueProperty&&!_syncing&&Input is not null)
        {
            _syncing=true;
            var value=_logarithmic?Math.Pow(10,Value)+_minimum-1:Value;
            Input.Value=(decimal)Math.Clamp(Math.Round(value/_step)*_step,_minimum,_maximum);_syncing=false;
        }
    }
    private void InputChanged(object? sender,AvaloniaPropertyChangedEventArgs change)
    {if(!_syncing&&(change.Property==NumericUpDown.ValueProperty||change.Property==NumericUpDown.MinimumProperty||change.Property==NumericUpDown.MaximumProperty||change.Property==NumericUpDown.IncrementProperty||change.Property==Control.IsEnabledProperty))Refresh();}
    private void Refresh()
    {
        if(Input is null)return;_syncing=true;
        _minimum=(double)Input.Minimum;_maximum=Math.Max(_minimum+.000001,Math.Min((double)Input.Maximum,1000000));
        _step=Math.Max(.001,(double)Input.Increment);_logarithmic=_maximum-_minimum>1000;
        double Scale(double value)=>_logarithmic?Math.Log10(Math.Max(0,value-_minimum)+1):value;
        Minimum=Scale(_minimum);Maximum=Scale(_maximum);Value=Scale(Math.Clamp((double)(Input.Value??Input.Minimum),_minimum,_maximum));
        SmallChange=(Maximum-Minimum)/100;LargeChange=SmallChange*10;IsEnabled=Input.IsEnabled;
        var name=Avalonia.Automation.AutomationProperties.GetName(Input);
        Avalonia.Automation.AutomationProperties.SetName(this,string.IsNullOrEmpty(name)?Input.Name??"调整数值":name);_syncing=false;
    }
}
