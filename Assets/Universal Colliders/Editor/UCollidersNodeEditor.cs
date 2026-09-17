/*! \file UCollidersNodeEditor.cs
    \brief A base file to use custom editor with UColliders
*/
using System.Text;
using UnityEditor;

/// <summary>
/// This namespace implements various Editor scripts derivating from the <c>Editor</c> class.
/// </summary>
namespace UColliders.EditorScripts {

    /// <summary>
    /// Base class for proper display of UCollider objects in the Editor.
    /// </summary>
    public abstract class UCollidersNodeEditor : Editor
    {
        /// <summary>
        /// shape property of the UCollidersNode.
        /// </summary>
        protected SerializedProperty shape;
        
        /// <summary>
        /// Boundicity property of the shape between 0 and 1.
        /// 
        /// Only useful when shape is Sphere or Capsule.
        /// </summary>
        protected SerializedProperty boundicity;

        
        /// <summary>
        /// Quick way to represent a quantity with a number. It avoids the classic parentheses "n objet(s)".
        /// </summary>
        /// <example>One should write "1 apples" but "6 apples".</example>
        /// <param name="quantity">The number of elements. Above 0 is considered plural.</param>
        /// <param name="element">The element you want to count.</param>
        /// <param name="plural_word">The plural version of the element.</param>
        /// <returns><c>quantity + " "</c> + {accorded element}</returns>
        public string Plural(int quantity, string element, string plural_word) {
            if (quantity > 1)
                return string.Format("{0} {1}", quantity, plural_word);
            return string.Format("{0} {1}", quantity, element);
        }

        /// <summary>
        /// Quick way to represent a quantity with a number. It avoids the classic parentheses "n objet(s)".
        /// </summary>
        /// <example>One should write "1 apple" but "6 apples".</example>
        /// <param name="quantity">The number of elements. Above 0 is considered plural.</param>
        /// <param name="element">The element you want to count.</param>
        /// <returns><c>quantity + " " + element</c> {+ "s" if necessary}.</returns>
        public string Plural(int quantity, string element) {
            return Plural(quantity, element, element + "s");
        }

        /// <summary>
        /// Compute and return various useful data for this node.
        /// </summary>
        /// <returns>Bullet points of data.</returns>
        public abstract string GetStatistics();

        /// <summary>
        /// Append a new row to the stringBuilder, with correctly accorded element.
        /// </summary>
        /// <param name="stringBuilder">The <c>StringBuilder</c> to be modified</param>
        /// <param name="quantity">Number of elements.</param>
        /// <param name="element">Singular name of the element.</param>
        public void AddStatisticsRow(StringBuilder stringBuilder, int quantity, string element) {
            stringBuilder.Append(string.Format("\n- {0}", Plural(quantity, element)));
        }
    
        /// <summary>
        /// Append a new row to the stringBuilder, with correctly accorded element.
        /// </summary>
        /// <param name="stringBuilder">The <c>StringBuilder</c> to be modified</param>
        /// <param name="quantity">Number of elements.</param>
        /// <param name="element">Singular name of the element.</param>
        /// <param name="plural_word">Plural name of the elemnt when there is more than one.</param>
        public void AddStatisticsRow(StringBuilder stringBuilder, int quantity, string element, string plural_word) {
            stringBuilder.Append(string.Format("\n- {0}", Plural(quantity, element, plural_word)));
        }
    }
}