package net.symbolon.core

/**
 * Fixed identifiers shared with the game side. These are part of the file / code
 * contract and must never change.
 */
object Contract {
    /** Prefix of a compressed map code. */
    const val MAP_CODE_PREFIX: String = "EKM1."

    /** Prefix of a compressed role code. */
    const val ROLE_CODE_PREFIX: String = "EKR1."

    /** File name suffix of a saved map. */
    const val MAP_FILE_SUFFIX: String = ".ekmap.json"

    /** File name suffix of a saved role. */
    const val ROLE_FILE_SUFFIX: String = ".ekr"

    /** Folder (under the game's data root) that holds saved maps. */
    const val MAPS_FOLDER: String = "EndKnot/EKMaps"

    /** Folder (under the game's data root) that holds saved roles. */
    const val ROLES_FOLDER: String = "EndKnot/EKRoles"
}
