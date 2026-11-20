package com.osfans.trime.link

import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import com.osfans.trime.data.theme.ThemeScope

internal fun keyboardGlass(scope: ThemeScope): Drawable? {
    if (scope.activeColorScheme?.containsKey("glass_highlight") != true) {
        return scope.drawable("keyboard_background")
    }
    return GradientDrawable(
        GradientDrawable.Orientation.TL_BR,
        intArrayOf(scope.color("glass_highlight"), scope.color("keyboard_back_color"), scope.color("glass_shade")),
    )
}
