﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using XUnity.Common.Constants;

namespace XUnity.AutoTranslator.Plugin.Core.Extensions
{
   internal static class GameObjectExtensions
   {
      private static GameObject[] _objects = new GameObject[ 128 ];
      private static readonly string XuaIgnore = "XUAIGNORE";

      public static Component GetFirstComponentInSelfOrAncestor( this GameObject go, Type type )
      {
         if( type == null ) return null;

         var current = go;

         while( current != null )
         {
            var foundComponent = current.GetComponent( type );
            if( foundComponent != null )
            {
               return foundComponent;
            }
            
            current = current.transform?.parent?.gameObject;
         }

         return null;
      }

      public static IEnumerable<Component> GetAllTextComponentsInChildren( this GameObject go )
      {
         if( ClrTypes.TMP_Text != null )
         {
            foreach( var comp in go.GetComponentsInChildren( ClrTypes.TMP_Text ) )
            {
               yield return comp;
            }
         }
         if( ClrTypes.Text != null )
         {
            foreach( var comp in go.GetComponentsInChildren( ClrTypes.Text ) )
            {
               yield return comp;
            }
         }
         if( ClrTypes.TextMesh != null )
         {
            foreach( var comp in go.GetComponentsInChildren( ClrTypes.TextMesh ) )
            {
               yield return comp;
            }
         }
         if( ClrTypes.UILabel != null )
         {
            foreach( var comp in go.GetComponentsInChildren( ClrTypes.UILabel ) )
            {
               yield return comp;
            }
         }
      }

      public static string[] GetPathSegments( this GameObject obj )
      {
         int i = 0;
         int j = 0;

         _objects[ i++ ] = obj;
         while( obj.transform.parent != null )
         {
            obj = obj.transform.parent.gameObject;
            _objects[ i++ ] = obj;
         }

         var result = new string[ i ];
         while( --i >= 0 )
         {
            result[ j++ ] = _objects[ i ].name;
            _objects[ i ] = null;
         }

         return result;
      }

      public static string GetPath( this GameObject obj )
      {
         StringBuilder path = new StringBuilder();
         var segments = GetPathSegments( obj );
         for( int i = 0; i < segments.Length; i++ )
         {
            path.Append( "/" ).Append( segments[ i ] );
         }

         return path.ToString();
      }

      public static bool HasIgnoredName( this GameObject go )
      {
         return go.name.Contains( XuaIgnore );
      }
   }
}
