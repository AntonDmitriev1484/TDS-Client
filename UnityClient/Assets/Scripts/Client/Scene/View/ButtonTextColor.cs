using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(UnityEngine.UI.Button))]
public class ButtonTextColor : MonoBehaviour,
    IPointerEnterHandler,
    IPointerDownHandler, IPointerUpHandler,
    ISelectHandler, IDeselectHandler
{
    [SerializeField] private TMP_Text _label;
    [SerializeField] private Color _normalColor = Color.white;
    [SerializeField] private Color _hoveredColor = Color.black;
    [SerializeField] private Color _pressedColor = Color.grey;
    [SerializeField] private Color _selectedColor = Color.black;

    private void Reset()
    {
        _label = GetComponentInChildren<TMP_Text>();
    }

    public void OnPointerEnter(PointerEventData e) => _label.color = _hoveredColor;
    public void OnPointerExit(PointerEventData e) => _label.color = _normalColor;
    public void OnPointerDown(PointerEventData e) => _label.color = _pressedColor;
    public void OnPointerUp(PointerEventData e) => _label.color = _hoveredColor;
    public void OnSelect(BaseEventData e) => _label.color = _selectedColor;
    public void OnDeselect(BaseEventData e) => _label.color = _normalColor;
}
